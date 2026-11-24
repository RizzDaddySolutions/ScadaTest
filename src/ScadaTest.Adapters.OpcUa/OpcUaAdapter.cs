using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using ScadaTest.Core;

namespace ScadaTest.Adapters.OpcUa;

/// <summary>
/// Tags over OPC UA for any server (AVEVA OI Gateway, Geo SCADA, a simulator), and optionally
/// simulator control through OPC UA methods.
/// </summary>
/// <remarks>
/// Settings:
/// <list type="bullet">
/// <item><c>endpoint</c> (required), e.g. <c>opc.tcp://sim-vm:4840</c></item>
/// <item><c>useSecurity</c> (default false), <c>autoAcceptCertificates</c> (default false), <c>pkiPath</c></item>
/// <item><c>username</c>, <c>password</c> (anonymous when omitted)</item>
/// <item><c>pollInterval</c> for subscriptions (default 250ms)</item>
/// <item>Simulator control (all NodeIds): <c>simulator.object</c>, <c>simulator.loadScenario</c>,
/// <c>simulator.reset</c>, <c>simulator.injectFault</c>, <c>simulator.state</c></item>
/// </list>
/// Tag addresses come from the tag map (adapter name = the environment's adapter key), so a map entry or a
/// template such as <c>ns=2;s={name}</c> turns <c>P-101.Running</c> into a NodeId.
/// </remarks>
[ScadaAdapter("opcua")]
public sealed class OpcUaAdapter : IScadaAdapter, ITagClient, ISimulatorControl
{
    private AdapterConfig _config = null!;
    private ISession? _session;
    private TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);
    private readonly ITelemetryContext _telemetry = DefaultTelemetry.Create(_ => { });

    public string Id => "opcua";

    public Capability Capabilities =>
        Capability.Tags | (_config?.Get("simulator.object") is null ? Capability.None : Capability.Simulator);

    private ISession Session => _session ?? throw new InvalidOperationException("OPC UA adapter is not connected.");

    public async Task ConnectAsync(AdapterConfig config, CancellationToken ct)
    {
        _config = config;
        var endpointUrl = config.Require("endpoint");
        var useSecurity = bool.TryParse(config.Get("useSecurity"), out var s) && s;
        var autoAccept = bool.TryParse(config.Get("autoAcceptCertificates"), out var a) && a;
        if (config.Get("pollInterval") is { } poll)
            _pollInterval = Duration.Parse(poll);

        var appConfig = await BuildConfigurationAsync(_telemetry, config.Get("pkiPath") ?? Path.Combine(AppContext.BaseDirectory, "pki"), autoAccept, ct)
            .ConfigureAwait(false);
        var endpointDescription = await CoreClientUtils.SelectEndpointAsync(appConfig, endpointUrl, useSecurity, 15000, _telemetry, ct).ConfigureAwait(false);
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(appConfig));

        IUserIdentity identity = config.Get("username") is { } user
            ? new UserIdentity(user, System.Text.Encoding.UTF8.GetBytes(config.Get("password") ?? string.Empty))
            : new UserIdentity(new AnonymousIdentityToken());

        _session = await new DefaultSessionFactory(_telemetry)
            .CreateAsync(appConfig, endpoint, false, false, $"ScadaTest-{config.Name}", 60000, identity, null, ct)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            _session.Dispose();
            _session = null;
        }
    }

    // ---- ITagClient -----------------------------------------------------------

    public async Task<TagValue> ReadAsync(TagRef tag, CancellationToken ct)
    {
        var dv = await Session.ReadValueAsync(Resolve(tag), ct).ConfigureAwait(false);
        return new TagValue(tag, dv.Value, ToQuality(dv.StatusCode), dv.SourceTimestamp == DateTime.MinValue
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(dv.SourceTimestamp, DateTimeKind.Utc)));
    }

    public async Task WriteAsync(TagRef tag, object? value, CancellationToken ct)
    {
        var nodeId = Resolve(tag);
        // Convert to the node's current .NET type so "true", 1 and 1.0 all write correctly.
        var current = await Session.ReadValueAsync(nodeId, ct).ConfigureAwait(false);
        var converted = ConvertTo(value, current.Value?.GetType());

        var request = new WriteValueCollection
        {
            new WriteValue { NodeId = nodeId, AttributeId = Attributes.Value, Value = new DataValue(new Variant(converted)) },
        };
        var response = await Session.WriteAsync(null, request, ct).ConfigureAwait(false);
        var status = response.Results[0];
        if (StatusCode.IsBad(status))
            throw new InvalidOperationException($"OPC UA write to '{tag}' ({nodeId}) failed: {status}.");
    }

    /// <summary>Polling subscription. Monitored items are a later improvement; polling is enough for test waits.</summary>
    public async IAsyncEnumerable<TagValue> SubscribeAsync(TagRef tag, [EnumeratorCancellation] CancellationToken ct)
    {
        object? last = null;
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            var v = await ReadAsync(tag, ct).ConfigureAwait(false);
            if (first || !Equals(v.Value, last))
            {
                first = false;
                last = v.Value;
                yield return v;
            }
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    // ---- ISimulatorControl --------------------------------------------------------

    public Task LoadScenarioAsync(string scenario, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        object[] args = parameters is { Count: > 0 }
            ? [scenario, JsonSerializer.Serialize(parameters)]
            : [scenario];
        return CallSimulatorAsync("simulator.loadScenario", ct, args);
    }

    public Task ResetAsync(CancellationToken ct) => CallSimulatorAsync("simulator.reset", ct);

    public Task SetInputAsync(TagRef input, object? value, CancellationToken ct) => WriteAsync(input, value, ct);

    public Task InjectFaultAsync(TagRef device, string fault, CancellationToken ct) =>
        CallSimulatorAsync("simulator.injectFault", ct, device.Name, fault);

    public async Task<string> GetScenarioStateAsync(CancellationToken ct)
    {
        var dv = await Session.ReadValueAsync(NodeId.Parse(_config.Require("simulator.state")), ct).ConfigureAwait(false);
        return Convert.ToString(dv.Value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task CallSimulatorAsync(string methodSetting, CancellationToken ct, params object[] args)
    {
        var objectId = NodeId.Parse(_config.Require("simulator.object"));
        var methodId = NodeId.Parse(_config.Require(methodSetting));
        await Session.CallAsync(objectId, methodId, ct, args).ConfigureAwait(false);
    }

    // ---- helpers ------------------------------------------------------------------

    private NodeId Resolve(TagRef tag) => NodeId.Parse(_config.TagMap.Resolve(tag, _config.Name));

    internal static Quality ToQuality(StatusCode status) =>
        StatusCode.IsGood(status) ? Quality.Good : StatusCode.IsUncertain(status) ? Quality.Uncertain : Quality.Bad;

    internal static object? ConvertTo(object? value, Type? target)
    {
        if (value is null || target is null || target.IsInstanceOfType(value))
            return value;
        if (target == typeof(bool) && ValueComparer.TryBool(value, out var b))
            return b;
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static async Task<ApplicationConfiguration> BuildConfigurationAsync(ITelemetryContext telemetry, string pkiPath, bool autoAccept, CancellationToken ct)
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "ScadaTest",
            ApplicationUri = $"urn:{Utils.GetHostName()}:ScadaTest",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiPath, "own"),
                    SubjectName = "CN=ScadaTest",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiPath, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiPath, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiPath, "rejected") },
                AutoAcceptUntrustedCertificates = autoAccept,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
        };

        var app = new ApplicationInstance(telemetry) { ApplicationName = config.ApplicationName, ApplicationType = ApplicationType.Client, ApplicationConfiguration = config };
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
        return config;
    }
}
