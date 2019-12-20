using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Castle.DynamicProxy;
using Microsoft.Azure.TypeEdge.Attributes;
using Microsoft.Azure.TypeEdge.Modules;
using Microsoft.Azure.TypeEdge.Proxy;
using Microsoft.Azure.TypeEdge.Volumes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using static System.String;
using ServiceDescriptor = Microsoft.Azure.TypeEdge.Description.ServiceDescriptor;

namespace Microsoft.Azure.TypeEdge
{
    public static class Startup
    {
        public static TypeModule Module { get; set; }

        public static Task DockerEntryPoint(string[] args)
        {
            return DockerEntryPoint(args, null);
        }

        public static async Task DockerEntryPoint(string[] args, Action<ContainerBuilder> registerServices)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += ctx => cancellationTokenSource.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cancellationTokenSource.Cancel();

            var services = new ServiceCollection().AddLogging();
            var containerBuilder = new ContainerBuilder();
            containerBuilder.Populate(services);

            if(registerServices != null)
            {
                registerServices(containerBuilder);
            }
            
            containerBuilder.RegisterBuildCallback(c => { });

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appSettings.json", true)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            var moduleName = configuration.GetValue<string>(Constants.ModuleNameConfigName);

            if (IsNullOrEmpty(moduleName))
            {
                moduleName = DiscoverModuleName();
                if (IsNullOrEmpty(moduleName))
                {
                    Console.WriteLine($"WARN:No {Constants.ModuleNameConfigName} in configuration. ");
                    Console.WriteLine("Exiting...");
                    return;
                }
            }

            configuration = new ConfigurationBuilder()
                .AddJsonFile("appSettings.json", true)
                .AddJsonFile($"{moduleName}Settings.json", true)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            var (moduleType, _) = GetModuleTypes(moduleName);

            if (moduleType == null)
                throw new ArgumentException($"No module called {moduleName} in calling assembly");

            if (configuration.GetValue("metadata", false))
            {
                var description = ServiceDescriptor.Describe(moduleType);
                Console.WriteLine(JsonConvert.SerializeObject(description, Formatting.Indented));
                return;
            }


            var _inContainer = File.Exists(@"/.dockerenv");
            var _in_Docker_Compose = Directory.Exists(Constants.ComposeConfigurationPath);
            if(_inContainer && _in_Docker_Compose)
            {
                //check the file system, we are in docker-compose mode
                var fileName = Path.Combine(Constants.ComposeConfigurationPath, $"{moduleName}.env");
                var remainingSeconds = 100;
                while (remainingSeconds-- > 0)
                {
                    if (File.Exists(fileName))
                    {
                        configuration = new ConfigurationBuilder()
                            .AddJsonFile("appSettings.json", true)
                            .AddJsonFile($"{moduleName}Settings.json", true)
                            .AddEnvironmentVariables()
                            .AddCommandLine(args)
                            .AddDotΕnv(fileName)
                            .Build();
                        File.Delete(fileName);
                        break;
                    }

                    Console.WriteLine($"{moduleName}:{fileName} does not exist. Retrying in 1 sec.");
                    Thread.Sleep(1000);
                }

                if (remainingSeconds < 0)
                {
                    Console.WriteLine($"{moduleName}:No {moduleName}.env found.");
                    Console.WriteLine($"{moduleName}:Exiting...");
                    return;
                }
            }

            containerBuilder.RegisterType(moduleType);
            containerBuilder.RegisterInstance(configuration);

            var moduleDependencies = moduleType.GetConstructors().First().GetParameters();
            var moduleDependencyTypes = moduleDependencies.Where(i => i.ParameterType.IsInterface &&
                                                                      i.ParameterType.GetCustomAttribute(
                                                                          typeof(TypeModuleAttribute),
                                                                          true) != null).Select(e => e.ParameterType);

            var proxyGenerator = new ProxyGenerator();
            foreach (var dependency in moduleDependencyTypes)
                containerBuilder.RegisterInstance(
                        proxyGenerator.CreateInterfaceProxyWithoutTarget(dependency, new ModuleProxyBase(dependency)))
                    .As(dependency);

            var container = containerBuilder.Build();

            Module = container.Resolve(moduleType) as TypeModule;
            if (Module != null)
            {
                Module._Init(configuration, container);
                await Module._RunAsync(cancellationTokenSource.Token);
            }

            await cancellationTokenSource.Token.WhenCanceled();
        }

        private static string DiscoverModuleName()
        {
            var assembly = Assembly.GetEntryAssembly();
            var moduleType = assembly.GetTypes().SingleOrDefault(t =>
                t.GetProxyInterface() != null);

            return moduleType?.GetProxyInterface().GetModuleName();
        }

        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>) s).SetResult(true), tcs);
            return tcs.Task;
        }

        private static (Type moduleType, Type moduleInterfaceType)
            GetModuleTypes(string moduleName)
        {
            return GetModule(moduleName, Assembly.GetEntryAssembly(), out var moduleTypes)
                ? moduleTypes
                : AppDomain.CurrentDomain.GetAssemblies()
                    .Any(assembly => GetModule(moduleName, assembly, out moduleTypes))
                    ? moduleTypes
                    : (null, null);
        }

        private static bool GetModule(string moduleName, Assembly assembly,
            out (Type moduleType, Type moduleInterfaceType) moduleTypes)
        {
            var moduleType = assembly.GetTypes().SingleOrDefault(t =>
                t.GetInterfaces().SingleOrDefault(i =>
                    i.GetCustomAttribute(typeof(TypeModuleAttribute), true) != null &&
                    string.Equals(i.GetModuleName(), moduleName, StringComparison.InvariantCultureIgnoreCase)) != null);

            if (moduleType == null)
            {
                moduleTypes = (null, null);
                return false;
            }

            var moduleInterfaceType = moduleType.GetProxyInterface();
            moduleTypes = (moduleType, moduleInterfaceType);
            return true;
        }
    }
}