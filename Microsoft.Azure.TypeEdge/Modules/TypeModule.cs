using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Castle.DynamicProxy;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.TypeEdge.Enums;
using Microsoft.Azure.TypeEdge.Modules.Endpoints;
using Microsoft.Azure.TypeEdge.Modules.Enums;
using Microsoft.Azure.TypeEdge.Modules.Messages;
using Microsoft.Azure.TypeEdge.Proxy;
using Microsoft.Azure.TypeEdge.Twins;
using Microsoft.Azure.TypeEdge.Volumes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("Microsoft.Azure.TypeEdge.Host")]
[assembly: InternalsVisibleTo("Microsoft.Azure.TypeEdge.Proxy")]

namespace Microsoft.Azure.TypeEdge.Modules
{
    public abstract class TypeModule : IDisposable
    {
        protected TypeModule()
        {
            _routeSubscriptions = new Dictionary<string, SubscriptionCallback>();
            _twinSubscriptions = new Dictionary<string, SubscriptionCallback>();

            _methodSubscriptions = new Dictionary<string, MethodCallback>();
            Volumes = new Dictionary<string, string>();

            DefaultTwin = new TwinCollection();
            Routes = new List<string>();

            Logger = TypeEdge.Logger.Factory.CreateLogger(GetType());

            InstantiateProperties();
        }

        public void Dispose()
        {
            Dispose(true);

            if (Volumes != null)
                foreach (var volume in Volumes)
                    try
                    {
                        Logger.LogInformation($"Directory {volume.Key} will not be deleted");
                        //todo:empty or not?
                        //if (Directory.Exists(item.Value))
                        //    Directory.Delete(item.Value);
                    }
                    catch
                    {
                        // ignored
                    }

            GC.SuppressFinalize(this);
        }


        protected T GetProxy<T>()
            where T : class
        {
            var cb = new ContainerBuilder();
            cb.RegisterInstance(new ProxyGenerator()
                .CreateInterfaceProxyWithoutTarget<T>(new ModuleProxy<T>()));
            return cb.Build().Resolve<T>();
        }

        protected HostingSettings GenerateHostingSettings()
        {
            //todo: add attributes for static env configuration
            var createOptions = new Dictionary<string, object>();
            var env = new List<string> {Constants.ModuleNameConfigName + "=" + Name};


            if (Volumes.Count > 0)
            {
                var volumes = string.Join(',', Volumes.Select(e => $"\"/env/{e.Key.ToLower()}\": {{ {e.Value} }}"));
                env.Add($", \"Volumes\": {{ {volumes} }}");
            }

            createOptions.Add("Env", env);
            return new HostingSettings(Name, createOptions);
        }

        internal virtual async Task<ExecutionResult> _RunAsync(CancellationToken cancellationToken)
        {
            RegisterMethods();

            // Open a connection to the Edge runtime
            if (string.IsNullOrEmpty(_connectionString))
                _ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(_transportSettings);
            else
                _ioTHubModuleClient = ModuleClient.CreateFromConnectionString(_connectionString, _transportSettings);

            await _ioTHubModuleClient.OpenAsync();
            Logger.LogInformation("IoT Hub module client initialized.");

            // Register callback to be called when a message is received by the module
            foreach (var subscription in _routeSubscriptions)
            {
                await _ioTHubModuleClient.SetInputMessageHandlerAsync(subscription.Key, MessageHandler,
                    subscription.Value);

                Logger.LogInformation($"MessageHandler set for {subscription.Key}");
            }

            // Register callback to be called when a twin update is received by the module
            await _ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(PropertyHandler, _twinSubscriptions);

            foreach (var subscription in _methodSubscriptions)
            {
                await _ioTHubModuleClient.SetMethodHandlerAsync(subscription.Key, MethodCallback, subscription.Value);
                Logger.LogInformation($"MethodCallback set for{subscription.Key}");
            }

            Logger.LogInformation("Running RunAsync..");
            return await RunAsync(cancellationToken);
        }

        internal virtual InitializationResult _Init(IConfigurationRoot configuration, IContainer container)
        {
            Logger.LogInformation("InternalConfigure called");

            _connectionString = configuration.GetValue<string>($"{Constants.EdgeHubConnectionStringKey}");
            if (string.IsNullOrEmpty(_connectionString))
                Logger.LogWarning($"Missing {Constants.EdgeHubConnectionStringKey} variable.");


            var settings =
                container.ResolveOptionalNamed<ITransportSettings>(Name + "TransportSettings")
                ?? container.ResolveOptional<ITransportSettings>()
                ?? DefaultTransport(configuration);


            _transportSettings = new ITransportSettings[] { settings };

            return Init();
        }

        internal async Task<PublishResult> PublishMessageAsync<T>(string outputName, T message)
            where T : IEdgeMessage
        {
            Logger.LogInformation("PublishMessageAsync called");
            var edgeMessage = new Message(message.GetBytes());


            if (message.Properties != null)
                foreach (var prop in edgeMessage.Properties)
                    edgeMessage.Properties.Add(prop.Key, prop.Value);

            await _ioTHubModuleClient.SendEventAsync(outputName, edgeMessage);

            var messageString = Encoding.UTF8.GetString(message.GetBytes());
            Logger.LogInformation($"Message: Body: [{messageString}]");

            return PublishResult.Ok;
        }

        internal void SubscribeRoute<T>(string outName, string outRoute, string inName, string inRoute,
            Func<T, Task<MessageResult>> handler)
            where T : IEdgeMessage
        {
            Logger.LogInformation("SubscribeRoute called");

            if (outRoute != "$downstream")
                Routes.Add($"FROM {outRoute} INTO {inRoute}");

            if (_routeSubscriptions.Keys.Contains(inName))
                throw new Exception($"Only one subscription allowed with name {inName} in Module {Name}");

            _routeSubscriptions[inName] = new SubscriptionCallback(inName, handler, typeof(T));
        }

        internal void SubscribeRoute(string outName, string outRoute, string inName, string inRoute)
        {
            Logger.LogInformation("SubscribeRoute called");

            if (outRoute != "$downstream")
                Routes.Add($"FROM {outRoute} INTO {inRoute}");
        }

        internal void SubscribeTwin<T>(string name, Func<T, Task<TwinResult>> handler) where T : TypeTwin
        {
            Logger.LogInformation("SubscribeTwin called");

            _twinSubscriptions[name] = new SubscriptionCallback(name, handler, typeof(T));
        }

        internal async Task ReportTwinAsync<T>(string name, T twin)
            where T : TypeTwin
        {
            Logger.LogInformation("ReportTwinAsync called");
            await _ioTHubModuleClient.UpdateReportedPropertiesAsync(twin.GetReportedProperties());
        }

        internal void RegisterVolume(string volumeName)
        {
            if (Volumes.Keys.Contains(volumeName))
                return;

            var volumePath = volumeName.ToLower();

            if (!Directory.Exists(volumePath)) Directory.CreateDirectory(volumePath);

            Volumes[volumeName] = volumePath;
        }

        internal T GetReferenceData<T>(string name, string index)
            where T : class, IEdgeMessage, new()
        {
            try
            {
                var path = Path.Combine(Volumes[name], index);
                if (File.Exists(path))
                {
                    var result = new T();
                    var bytes = File.ReadAllBytes(path);
                    result.SetBytes(bytes);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in GetReferenceData.");
            }

            return null;
        }

        internal bool SetReferenceData<T>(string name, string index, T value)
            where T : class, IEdgeMessage, new()
        {
            try
            {
                var path = Path.Combine(Volumes[name], index);
                var bytes = value.GetBytes();

                File.WriteAllBytes(path, bytes);

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in SetReferenceData.");
            }

            return false;
        }

        internal bool DeleteReferenceData(string name, string index)
        {
            try
            {
                var path = Path.Combine(Volumes[name], index);
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in DeleteReferenceData.");
            }

            return false;
        }

        #region members

        private readonly Dictionary<string, MethodCallback> _methodSubscriptions;
        private readonly Dictionary<string, SubscriptionCallback> _routeSubscriptions;
        private readonly Dictionary<string, SubscriptionCallback> _twinSubscriptions;
        private ModuleClient _ioTHubModuleClient;
        private string _connectionString;
        private ITransportSettings[] _transportSettings;

        #endregion

        #region Properties

        public virtual string Name
        {
            get
            {
                var proxyInterface = GetType().GetProxyInterface();

                if (proxyInterface == null)
                    throw new ArgumentException(
                        $"{GetType().Name} has needs to implement an single interface annotated with the TypeModule Attribute");

                return proxyInterface.GetModuleName();
            }
        }

        internal virtual HostingSettings HostingSettings => GenerateHostingSettings();

        #endregion

        #region properties

        internal Dictionary<string, string> Volumes { get; }
        internal virtual List<string> Routes { get; }
        internal virtual TwinCollection DefaultTwin { get; private set; }
        protected ILogger Logger { get; }

        #endregion

        #region virtual methods

        internal virtual async Task<T> PublishTwinAsync<T>(string name, T twin)
            where T : TypeTwin, new()
        {
            await _ioTHubModuleClient.UpdateReportedPropertiesAsync(twin.GetReportedProperties());
            return twin;
        }

        internal virtual async Task<T> GetTwinAsync<T>(string name)
            where T : TypeTwin, new()
        {
            var twin = await _ioTHubModuleClient.GetTwinAsync();
            return TypeTwin.CreateTwin<T>(name, twin);
        }

        internal virtual void SetTwinDefault<T>(string name, T twin)
            where T : TypeTwin, new()
        {
            var partialTwin = twin.GetReportedProperties(name);

            var mergeSettings = new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Union
            };

            var union = JObject.Parse(DefaultTwin.ToJson());

            union.Merge(JObject.Parse(partialTwin.ToJson()), mergeSettings);

            DefaultTwin = new TwinCollection(union.ToString(Formatting.None));
        }

        public virtual Task<ExecutionResult> RunAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ExecutionResult.Ok);
        }

        public virtual InitializationResult Init()
        {
            return InitializationResult.Ok;
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        #endregion

        #region private methods

        private static ITransportSettings DefaultTransport(IConfigurationRoot configuration)
        {
            var settings = new AmqpTransportSettings(TransportType.Amqp_Tcp_Only);

            var disableSslCertificateValidationKey = configuration.GetValue($"{Constants.DisableSslCertificateValidationKey}", false);

            if (disableSslCertificateValidationKey)
            {
                settings.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>  true;
            }

            return settings;
        }

        private Task<MethodResponse> MethodCallback(MethodRequest methodRequest, object userContext)
        {
            Logger.LogInformation("MethodCallback called");
            if (!(userContext is MethodCallback callback))
                throw new InvalidOperationException($"{Name}:UserContext doesn't contain a valid SubscriptionCallback");

            var paramValues = JsonConvert.DeserializeObject<object[]>(methodRequest.DataAsJson);

            try
            {
                var paramTypes = callback.MethodInfo.GetParameters();

                for (var i = 0; i < paramTypes.Length; i++)
                    paramValues[i] = Convert.ChangeType(paramValues[i], paramTypes[i].ParameterType);

                var res = callback.MethodInfo.Invoke(this, paramValues);
                return Task.FromResult(
                    new MethodResponse(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(res)), 200));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in MethodCallback.");
                var result = "{\"result\":\"" + ex.Message + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }
        }

        private async Task<MessageResponse> MessageHandler(Message message, object userContext)
        {
            Logger.LogInformation("MessageHandler called");

            if (!(userContext is SubscriptionCallback callback))
                throw new InvalidOperationException("UserContext doesn't contain a valid SubscriptionCallback");

            var messageBytes = message.GetBytes();
            var messageString = Encoding.UTF8.GetString(messageBytes);

            Logger.LogDebug($"Message text:{messageString}");

            if (!(Activator.CreateInstance(callback.Type) is IEdgeMessage input))
            {
                Logger.LogWarning("Abandoned Message");
                return MessageResponse.Abandoned;
            }

            input.SetBytes(messageBytes);

            foreach (var messageProperty in message.Properties)
                input.Properties.Add(messageProperty.Key, messageProperty.Value);

            var invocationResult = callback.Handler.DynamicInvoke(input);
            var result = await (Task<MessageResult>) invocationResult;

            return result == MessageResult.Ok ? MessageResponse.Completed : MessageResponse.Abandoned;
        }

        private async Task PropertyHandler(TwinCollection desiredProperties, object userContext)
        {
            Logger.LogInformation("PropertyHandler called");

            if (!(userContext is Dictionary<string, SubscriptionCallback> callbacks))
                throw new InvalidOperationException("UserContext doesn't contain a valid SubscriptionCallback");

            Logger.LogInformation("Desired property change:");
            Logger.LogInformation(JsonConvert.SerializeObject(desiredProperties));

            foreach (var callback in callbacks)
                if (desiredProperties.Contains($"___{callback.Key}"))
                {
                    var input = TypeTwin.CreateTwin(callback.Value.Type, callback.Key, desiredProperties);

                    var invocationResult = callback.Value.Handler.DynamicInvoke(input);
                    await (Task<TwinResult>) invocationResult;
                }
        }

        private void InstantiateProperties()
        {
            var props = GetType().GetProperties();

            foreach (var prop in props)
            {
                var type = prop.PropertyType;

                if (!type.IsGenericType)
                    continue;

                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(Input<>)
                    || genericDef == typeof(Output<>)
                    || genericDef == typeof(ModuleTwin<>)
                    || genericDef == typeof(Volume<>))
                {
                    if (!prop.CanWrite)
                        throw new Exception($"{prop.Name} needs to be set dynamically, please define a setter.");

                    var name = $"{prop.Name}";
                    var value = Activator.CreateInstance(
                        type.GetGenericTypeDefinition().MakeGenericType(type.GenericTypeArguments), name, this);
                    prop.SetValue(this, value);
                }
            }
        }

        private void RegisterMethods()
        {
            Logger.LogInformation("RegisterMethods called");
            var interfaceType = GetType().GetProxyInterface();

            if (interfaceType == null)
                return;

            var moduleMethods = GetType().GetInterfaceMap(interfaceType).TargetMethods.Where(e => !e.IsSpecialName);
            foreach (var method in moduleMethods)
                _methodSubscriptions[method.Name] = new MethodCallback(method.Name, method);
        }

        #endregion
    }
}