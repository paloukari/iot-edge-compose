﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Castle.DynamicProxy;
using TypeEdge.Attributes;
using TypeEdge.DovEnv;
using TypeEdge.Modules;
using TypeEdge.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static System.String;

namespace TypeEdge
{
    public static class Startup
    {
        public static TypeModule Module { get; set; }

        public static async Task DockerEntryPoint(string[] args)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cancellationTokenSource.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cancellationTokenSource.Cancel();
            
            var services = new ServiceCollection().AddLogging();
            var containerBuilder = new ContainerBuilder();
            containerBuilder.Populate(services);
            containerBuilder.RegisterBuildCallback(c => { });

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            var moduleName = configuration.GetValue<string>(Constants.ModuleNameConfigName);

            if (IsNullOrEmpty(moduleName))
            {
                Console.WriteLine($"WARN:No {Constants.ModuleNameConfigName} in configuration. ");

                moduleName = DiscoverModuleName();
                if (IsNullOrEmpty(moduleName))
                {
                    Console.WriteLine("Exiting...");
                    return;
                    //throw new ArgumentException($"No moduleName in arguments");
                }
            }

            configuration = new ConfigurationBuilder()
             .AddJsonFile("appsettings.json", true)
             .AddJsonFile($"{moduleName}Settings.json", true)
             .AddEnvironmentVariables()
             .AddCommandLine(args)
             .Build();

            var (moduleType, _) = GetModuleTypes(moduleName);

            if (moduleType == null)
                throw new ArgumentException($"No module called {moduleName} in calling assembly");

            var connectionString = configuration.GetValue<string>($"{Constants.EdgeHubConnectionStringKey}");

            if (IsNullOrEmpty(connectionString))
            {

                //check the file system, we are in docker-compose mode
                var fileName = Path.Combine(Constants.ComposeConfigurationPath, $"{moduleName}.env");
                var remainingSeconds = 100;
                while (remainingSeconds-- > 0)
                {
                    if (File.Exists(fileName))
                    {
                        configuration = new ConfigurationBuilder()
                            .AddJsonFile("appsettings.json", true)
                            .AddJsonFile($"{moduleName}Settings.json", true)
                            .AddEnvironmentVariables()
                            .AddCommandLine(args)
                            .AddDotenvFile(fileName)
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

            var moduleDepedencies = moduleType.GetConstructors().First().GetParameters();
            var moduleDepedencyTypes = moduleDepedencies.Where(i => i.ParameterType.IsInterface &&
                                                                    i.ParameterType.GetCustomAttribute(
                                                                        typeof(TypeModuleAttribute),
                                                                        true) != null).Select(e => e.ParameterType);

            var proxyGenerator = new ProxyGenerator();
            foreach (var depedency in moduleDepedencyTypes)
            {
                containerBuilder.RegisterInstance(
                    proxyGenerator.CreateInterfaceProxyWithoutTarget(depedency, new ModuleProxyBase(depedency)))
                    .As(depedency);
            }

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
                t.GetInterfaces().SingleOrDefault(i =>
                    i.GetCustomAttribute(typeof(TypeModuleAttribute), true) != null) != null);

            if (moduleType == null)
                return null;

            return moduleType.GetProxyInterface().GetModuleName();
        }

        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        private static (Type moduleType, Type moduleInterfaceType)
            GetModuleTypes(string moduleName)
        {

            if (GetModule(moduleName, Assembly.GetEntryAssembly(), out var moduleTypes))
                return moduleTypes;

            //try all assemblies now
            return AppDomain.CurrentDomain.GetAssemblies().Any(assembly => GetModule(moduleName, assembly, out moduleTypes)) ? moduleTypes : (null, null);
        }

        private static bool GetModule(string moduleName, Assembly assembly,
            out (Type moduleType, Type moduleInterfaceType) moduleTypes)
        {
            var moduleType = assembly.GetTypes().SingleOrDefault(t =>
                t.GetInterfaces().SingleOrDefault(i =>
                    i.GetCustomAttribute(typeof(TypeModuleAttribute), true) != null &&
                    String.Equals(i.GetModuleName(), moduleName, StringComparison.InvariantCultureIgnoreCase)) != null);

            if (moduleType == null)
            {
                moduleTypes = (null, null);
                return false;
            }

            var moduleInterfaceType = moduleType.GetProxyInterface();
            moduleTypes = (moduleType, moduleInterfaceType);
            return true;
        }

        public static Type GetProxyInterface(this Type type)
        {
            return type.GetInterfaces()
                .SingleOrDefault(i => i.GetCustomAttribute(typeof(TypeModuleAttribute), true) != null);
        }
    }
}