using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Castle.DynamicProxy;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Common.Exceptions;
using Microsoft.Azure.Devices.Edge.Agent.Core;
using Microsoft.Azure.Devices.Edge.Storage;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.TypeEdge.Host.Docker;
using Microsoft.Azure.TypeEdge.Host.Hub;
using Microsoft.Azure.TypeEdge.Modules;
using Microsoft.Azure.TypeEdge.Modules.Endpoints;
using Microsoft.Azure.TypeEdge.Modules.Messages;
using Microsoft.Azure.TypeEdge.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using HubService = Microsoft.Azure.Devices.Edge.Hub.Service;
using IotHubConnectionStringBuilder = Microsoft.Azure.Devices.IotHubConnectionStringBuilder;
using Module = Microsoft.Azure.Devices.Module;
using TransportType = Microsoft.Azure.Devices.Client.TransportType;

namespace Microsoft.Azure.TypeEdge.Host
{
    public class TypeEdgeHost
    {
        private readonly ContainerBuilder _containerBuilder;

        private readonly string _deviceId;
        private readonly ModuleCollection _externalModules;
        private readonly EdgeHub _hub;
        private readonly bool _inContainer;
        private readonly string _iotHubConnectionString;
        private IContainer _container;
        private string _manifest;
        private ModuleCollection _modules;


        public TypeEdgeHost(IConfigurationRoot configuration)
        {
            _deviceId = configuration.GetValue<string>("DeviceId");
            _iotHubConnectionString = configuration.GetValue<string>("IotHubConnectionString");


            if (string.IsNullOrEmpty(_iotHubConnectionString))
                throw new Exception("Missing \"IotHubConnectionString\" value in configuration");

            if (string.IsNullOrEmpty(_deviceId))
                throw new Exception("Missing \"DeviceId\"value in configuration");

            _containerBuilder = new ContainerBuilder();
            _hub = new EdgeHub();

            Upstream = new Upstream<JsonMessage>(_hub);

            _inContainer = File.Exists(@"/.dockerenv");  

            _externalModules = new ModuleCollection();
        }

        public Upstream<JsonMessage> Upstream { get; set; } 

        public void RegisterModule<TIModule, TTModule>()
            where TIModule : class
            where TTModule : class
        {
            var moduleName = typeof(TIModule).GetModuleName();

            _containerBuilder.RegisterType<TTModule>().WithParameter(
                (pi, ctx) => pi.ParameterType == typeof(IConfigurationRoot),
                (pi, ctx) => new ConfigurationBuilder()
                    .AddJsonFile($"{moduleName}Settings.json", true)
                    .AddJsonFile("appSettings.json", true)
                    .AddEnvironmentVariables()
                    .Build());

            _containerBuilder.RegisterInstance(new ProxyGenerator()
                .CreateInterfaceProxyWithoutTarget<TIModule>(new ModuleProxy<TIModule>()));
        }


        public void RegisterExternalModule(ExternalModule externalModule)
        {
            _externalModules.Add(externalModule);
        }

        public void RegisterModule<TTModule>()
            where TTModule : class
        {
            _containerBuilder.RegisterType<TTModule>();
        }

        private void Init()
        {
            CleanUp();

            BuildDiContainer();

            _modules = CreateModules();
        }

        public string GenerateDeviceManifest(
            Func<string, DockerHostingSettings, DockerHostingSettings> hostingSettings = null)
        {
            if (_modules == null)
                Init();

            return _GenerateManifest(hostingSettings);
        }

        public string ProvisionDevice(string manifest)
        {
            if (_modules == null)
                Init();

            _manifest = manifest;

            var deviceSasKey = ProvisionDeviceAsync(manifest).Result;

            return deviceSasKey;
        }

        public void BuildEmulatedDevice(string deviceSasKey)
        {
            if (_modules == null)
                Init();

            ConfigureModules();

            BuildHub(deviceSasKey);
        }

        private void CleanUp()
        {
            if (!_inContainer || !Directory.Exists(Volumes.Constants.ComposeConfigurationPath))
                return;
            foreach (var file in Directory.EnumerateFiles(Volumes.Constants.ComposeConfigurationPath))
                File.Delete(file);
        }

        public async Task RunAsync()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += ctx => cancellationTokenSource.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cancellationTokenSource.Cancel();

            await RunAsync(cancellationTokenSource.Token);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var tasks = new List<Task>
            {
                CreateTemporaryConnection(),
                _hub.RunAsync(cancellationToken)
            };

            //start all modules
            if (!_inContainer)
                foreach (var module in _modules)
                    tasks.Add(module._RunAsync(cancellationToken));

            tasks.Add(cancellationToken.WhenCanceled());

            await Task.WhenAll(tasks.ToArray());
        }

        private async Task CreateTemporaryConnection()
        {
            var moduleConnectionString =
                GetModuleConnectionStringAsync(_iotHubConnectionString, _deviceId, _modules[0].Name)
                    .Result;

            var transportSettings = 
                _container.ResolveOptionalNamed<ITransportSettings>(nameof(TypeEdgeHost) + "TransportSettings")
                ?? _container.ResolveOptional<ITransportSettings>()
                ?? new AmqpTransportSettings(TransportType.Amqp_Tcp_Only)
                {
                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true,
                    OpenTimeout = new TimeSpan(1)
                };

            var tmpClient = ModuleClient.CreateFromConnectionString(moduleConnectionString,
                new ITransportSettings[]
                {
                    transportSettings
                }
            );

            try
            {
                await tmpClient.OpenAsync();
            }
            catch
            {
                // ignored
            }
        }

        public T GetProxy<T>()
            where T : class
        {
            var cb = new ContainerBuilder();
            cb.RegisterInstance(new ProxyGenerator()
                .CreateInterfaceProxyWithoutTarget<T>(new ModuleProxy<T>()));
            return cb.Build().Resolve<T>();
        }


        #region Build

        private void BuildDiContainer()
        {
            var services = new ServiceCollection().AddSingleton(new LoggerFactory()
                .AddConsole()
                .AddSerilog()
                .AddDebug());
            services.AddLogging();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .CreateLogger();

            _containerBuilder.Populate(services);
            _containerBuilder.RegisterBuildCallback(c => { });

            _containerBuilder.Register((ss, p) => new ConfigurationBuilder()
                .AddJsonFile("Settings.json", true)
                .AddJsonFile("appSettings.json", true)
                .AddEnvironmentVariables()
                .Build());

            _container = _containerBuilder.Build();
        }

        private void BuildHub(string deviceSasKey)
        {
            var currentLocation = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location); 
            if (_inContainer)
                currentLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

            Environment.SetEnvironmentVariable(HubService.Constants.ConfigKey.EdgeHubDevServerCertificateFile,
                Path.Combine(currentLocation, @"Certificates/edge-hub-dev/edge-hub-server.ca.pem"));

            Environment.SetEnvironmentVariable(HubService.Constants.ConfigKey.EdgeHubDevServerPrivateKeyFile,
                Path.Combine(currentLocation, @"Certificates/edge-hub-dev/edge-hub-server.pem"));

            Environment.SetEnvironmentVariable(HubService.Constants.ConfigKey.EdgeHubDevTrustBundleFile,
                Path.Combine(currentLocation, @"Certificates/edge-hub-dev/edge-hub-server.ca.pem"));

            Environment.SetEnvironmentVariable("AuthenticationMode","Cloud");

            var storageFolder = Path.Combine(currentLocation, @"Storage");

            var hubStorageFolder = Path.Combine(storageFolder, HubService.Constants.EdgeHubStorageFolder);

            if (!Directory.Exists(hubStorageFolder))
                Directory.CreateDirectory(hubStorageFolder);

            Environment.SetEnvironmentVariable("storageFolder", storageFolder);

            var csBuilder = IotHubConnectionStringBuilder.Create(_iotHubConnectionString);

            var edgeConnectionString =
                new ModuleConnectionStringBuilder(csBuilder.HostName, _deviceId)
                    .Create(Devices.Edge.Agent.Core.Constants.EdgeHubModuleIdentityName)
                    .WithSharedAccessKey(deviceSasKey)
                    .Build();
            Environment.SetEnvironmentVariable(Devices.Edge.Agent.Core.Constants.EdgeHubConnectionStringKey,
                edgeConnectionString);

            Environment.SetEnvironmentVariable(Devices.Edge.Agent.Core.Constants.IotHubConnectionStringKey,
                edgeConnectionString);

            var edgeHubConfiguration = new ConfigurationBuilder()
                .AddJsonFile("appSettings.json", true)
                .AddEnvironmentVariables()
                .Build();

            _hub._Init(edgeHubConfiguration, _container);
        }

        private void ConfigureModules()
        {
            var certificatePath = @"Certificates/edge-device-ca/cert/edge-device-ca-root.cert.pem";
            var currentLocation = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location);
            if(_inContainer)
                currentLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);


            foreach (var module in _modules)
            {
                if (module.HostingSettings.IsExternalModule)

                    Environment.SetEnvironmentVariable(Devices.Edge.Agent.Core.Constants.EdgeModuleCaCertificateFileKey,
                        Path.Combine(currentLocation,
                            certificatePath));
                else
                    Environment.SetEnvironmentVariable(Devices.Edge.Agent.Core.Constants.EdgeModuleCaCertificateFileKey,
                        "/azure-edge/vol1/edge-device-ca-root.cert.pem");

                var moduleConnectionString =
                    GetModuleConnectionStringAsync(_iotHubConnectionString, _deviceId, module.Name)
                        .Result;

                if (_inContainer)
                {
                    Console.WriteLine("Emulator running in docker-compose mode.");

                    const string envPath = Volumes.Constants.ComposeConfigurationPath;
                    var path = Path.Combine(envPath, $"{module.Name}.env");
                    var certPath = Path.Combine(envPath, certificatePath);

                    var dotEnvData =
                        $"{Devices.Edge.Agent.Core.Constants.EdgeHubConnectionStringKey}={moduleConnectionString}";
                    dotEnvData +=
                        $"{Environment.NewLine}{Devices.Edge.Agent.Core.Constants.EdgeModuleCaCertificateFileKey}={certPath}";
                    dotEnvData += $"{Environment.NewLine}{Volumes.Constants.DisableSslCertificateValidationKey}=true";

                    if (!Directory.Exists(Path.GetDirectoryName(certPath)))
                        Directory.CreateDirectory(Path.GetDirectoryName(certPath));

                    if (!File.Exists(certPath))
                        File.Copy(Path.Combine(currentLocation, certificatePath), certPath);

                    File.WriteAllText(path, dotEnvData);
                }
                else
                {
                    Environment.SetEnvironmentVariable(Devices.Edge.Agent.Core.Constants.EdgeHubConnectionStringKey,
                        moduleConnectionString);

                    Environment.SetEnvironmentVariable(Volumes.Constants.DisableSslCertificateValidationKey,
                        "true");

                    var moduleConfiguration = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string>
                            {{Constants.ManifestEnvironmentName, _manifest}})
                        .AddJsonFile($"{module.Name}Settings.json", true)
                        .AddJsonFile("appSettings.json", true)
                        .AddEnvironmentVariables()
                        .AddInMemoryCollection(
                            new Dictionary<string, string>
                            {
                                {Devices.Edge.Agent.Core.Constants.EdgeModuleVolumeNameKey, currentLocation},
                                {Devices.Edge.Agent.Core.Constants.EdgeModuleVolumePathKey, "/azure-edge/vol1"}
                            })
                        .Build();

                    module._Init(moduleConfiguration, _container);
                }
            }
        }

        private ModuleCollection CreateModules()
        {
            var modules = new ModuleCollection();
            using (var scope = _container.BeginLifetimeScope())
            {
                foreach (var moduleType in _container.ComponentRegistry.Registrations
                    .Where(r => typeof(TypeModule).IsAssignableFrom(r.Activator.LimitType))
                    .Select(r => r.Activator.LimitType).Distinct())
                    if (scope.Resolve(moduleType) is TypeModule module)
                        modules.Add(module);
            }

            modules.AddRange(_externalModules);

            return modules;
        }

        private string _GenerateManifest(Func<string, DockerHostingSettings, DockerHostingSettings> hostOverride)
        {
            ConfigurationContent configurationContent;

            using (var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Microsoft.Azure.TypeEdge.Host.deviceconfig.json"))
            using (var reader = new StreamReader(stream))
            {
                var deviceConfig = reader.ReadToEnd();
                configurationContent = JsonConvert.DeserializeObject<ConfigurationContent>(deviceConfig);
            }

            var agentDesired =
                JObject.FromObject(configurationContent.ModulesContent["$edgeAgent"]["properties.desired"]);

            if (!agentDesired.TryGetValue("modules", out var modules))
                throw new Exception("Cannot read modules config from $edgeAgent");

            if (!agentDesired.TryGetValue("systemModules", out var systemModules))
                throw new Exception("Cannot read systemModules config from $edgeAgent");

            var modulesConfig = modules as JObject;

            if (hostOverride != null)
                foreach (var systemModule in (systemModules as JObject))
                {
                    var dockerHostingSettings = JsonConvert.DeserializeObject<DockerHostingSettings>((systemModules as JObject)[systemModule.Key].ToJson());
                    dockerHostingSettings.IsSystemModule = true;

                    var settings = hostOverride(systemModule.Key, dockerHostingSettings);

                    systemModules[systemModule.Key]=JObject.Parse(JsonConvert.SerializeObject(settings,
                            Newtonsoft.Json.Formatting.None,
                            new JsonSerializerSettings
                            {
                                NullValueHandling = NullValueHandling.Ignore
                            }));

                }
            foreach (var module in _modules)
            {
                var dockerHostingSettings = new DockerHostingSettings(module.HostingSettings);
                var settings = hostOverride != null
                    ? hostOverride(module.Name, dockerHostingSettings)
                    : dockerHostingSettings;
                modulesConfig?.Add(module.Name, JObject.FromObject(settings));
                if (settings.IsExternalModule && module is DockerModule dockerModule)
                    dockerModule.DockerHostingSettings = settings;
            }

            foreach (var module in _modules)
                if (module.DefaultTwin?.Count > 0)
                {
                    if (!configurationContent.ModulesContent.ContainsKey(module.Name))
                        configurationContent.ModulesContent.Add(module.Name, new Dictionary<string, object>());

                    configurationContent.ModulesContent[module.Name]["properties.desired"] =
                        JObject.Parse(module.DefaultTwin.ToJson());
                }

            agentDesired["modules"] = modulesConfig;
            configurationContent.ModulesContent["$edgeAgent"]["properties.desired"] = agentDesired;

            var routes = new Dictionary<string, string>();

            foreach (var module in _modules)
                if (module.Routes != null)
                    foreach (var route in module.Routes)
                        routes[$"route{routes.Count}"] = route;

            foreach (var route in _hub.Routes) routes[$"route{routes.Count}"] = route;

            configurationContent.ModulesContent["$edgeHub"] = new Dictionary<string, object>
            {
                ["properties.desired"] = new
                {
                    schemaVersion = "1.0",
                    routes,
                    storeAndForwardConfiguration = new
                    {
                        timeToLiveSecs = 20
                    }
                }
            };

            _manifest = JToken.Parse(configurationContent.ToJson()).ToString(Formatting.Indented);

            return _manifest;
        }

        private async Task<string> ProvisionDeviceAsync(string manifest)
        {
            IotHubConnectionStringBuilder.Create(_iotHubConnectionString);
            
            var registryManager = RegistryManager.CreateFromConnectionString(_iotHubConnectionString);
            var device = await registryManager.GetDeviceAsync(_deviceId) ?? await registryManager.AddDeviceAsync(
                             new Device(_deviceId));
            var sasKey = device.Authentication.SymmetricKey.PrimaryKey;

            try
            {
                var oldModules = await registryManager.GetModulesOnDeviceAsync(_deviceId);
                foreach (var oldModule in oldModules)
                    if (!oldModule.Id.StartsWith("$"))
                        await registryManager.RemoveModuleAsync(oldModule);
            }
            catch
            {
                // ignored
            }

            foreach (var module in _modules)
                try
                {
                    await registryManager.AddModuleAsync(new Module(_deviceId, module.Name));
                }
                catch (ModuleAlreadyExistsException)
                {
                }

            await registryManager.ApplyConfigurationContentOnDeviceAsync(_deviceId,
                manifest.FromJson<ConfigurationContent>());

            return sasKey;
        }

        private async Task<string> GetModuleConnectionStringAsync(string iotHubConnectionString, string deviceId,
            string moduleName)
        {
            var csBuilder = IotHubConnectionStringBuilder.Create(iotHubConnectionString);
            var registryManager = RegistryManager.CreateFromConnectionString(iotHubConnectionString);
            var module = await registryManager.GetModuleAsync(deviceId, moduleName);
            var sasKey = module.Authentication.SymmetricKey.PrimaryKey;

            return new ModuleConnectionStringBuilder(csBuilder.HostName, deviceId)
                .Create(moduleName)
                .WithGatewayHostName(Environment.MachineName)
                .WithSharedAccessKey(sasKey)
                .Build();
        }

        public void RegisterInstance<T>(T instance) where T : class
        {
            _containerBuilder.RegisterInstance(instance);
        }

       public void RegisterTransportSettings(ITransportSettings transportSettings)
       {
           _containerBuilder.RegisterInstance(transportSettings);
       }

       public void RegisterTransportSettingsForHost(ITransportSettings transportSettings)
       {
           _containerBuilder.RegisterInstance(transportSettings).Named<ITransportSettings>(nameof(TypeEdgeHost) + "TransportSettings");
       }

       public void RegisterTransportSettingsForModule<T>(ITransportSettings transportSettings)
       {
           this.RegisterTransportSettingsForModule(transportSettings, typeof(T));
       }

       public void RegisterTransportSettingsForModule(ITransportSettings transportSettings, Type moduleType)
       {
           var moduleName = moduleType.GetProxyInterface().GetModuleName();
           _containerBuilder.RegisterInstance(transportSettings).Named<ITransportSettings>(moduleName + "TransportSettings");
       }

        #endregion
    }
}