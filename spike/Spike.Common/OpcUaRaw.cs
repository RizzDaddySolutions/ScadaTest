using System.Globalization;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace ScadaTest.Spike;

/// <summary>
/// A raw OPC UA session for discovery (browse, method signatures, calls with outputs). Reading and writing in
/// the spikes goes through the real <c>OpcUaAdapter</c>; this mirrors its connection settings so both behave the same.
/// </summary>
public sealed class OpcUaRaw : IAsyncDisposable
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });

    public ISession Session { get; }

    public EndpointDescription Endpoint { get; }

    private OpcUaRaw(ISession session, EndpointDescription endpoint)
    {
        Session = session;
        Endpoint = endpoint;
    }

    /// <summary>Shared client PKI folder so every spike program presents the same certificate to the servers.</summary>
    public static string DefaultPkiPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScadaTest", "pki");

    public static async Task<OpcUaRaw> ConnectAsync(
        string endpointUrl, bool useSecurity, bool autoAccept, string? username, string? password, string pkiPath, CancellationToken ct)
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

        var app = new ApplicationInstance(Telemetry) { ApplicationName = config.ApplicationName, ApplicationType = ApplicationType.Client, ApplicationConfiguration = config };
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);

        var description = await CoreClientUtils.SelectEndpointAsync(config, endpointUrl, useSecurity, 15000, Telemetry, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No endpoint found at {endpointUrl}.");
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));
        IUserIdentity identity = username is not null
            ? new UserIdentity(username, System.Text.Encoding.UTF8.GetBytes(password ?? string.Empty))
            : new UserIdentity(new AnonymousIdentityToken());

        var session = await new DefaultSessionFactory(Telemetry)
            .CreateAsync(config, endpoint, false, false, "ScadaTest-spike", 60000, identity, null, ct)
            .ConfigureAwait(false);
        return new OpcUaRaw(session, description);
    }

    public string Describe() =>
        $"{Endpoint.EndpointUrl} security {Endpoint.SecurityMode} / {Endpoint.SecurityPolicyUri?.Split('#').LastOrDefault()}, server {Endpoint.Server?.ApplicationName}";

    /// <summary>All forward hierarchical references of a node, following continuation points.</summary>
    public async Task<IReadOnlyList<ReferenceDescription>> BrowseAsync(NodeId node, CancellationToken ct)
    {
        var request = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = node,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All,
            },
        };
        var response = await Session.BrowseAsync(null, null, 0, request, ct).ConfigureAwait(false);
        var result = response.Results[0];
        if (StatusCode.IsBad(result.StatusCode))
            throw new InvalidOperationException($"Browse of {node} failed: {result.StatusCode}.");

        var all = new List<ReferenceDescription>(result.References);
        var continuation = result.ContinuationPoint;
        while (continuation is { Length: > 0 })
        {
            var next = await Session.BrowseNextAsync(null, false, [continuation], ct).ConfigureAwait(false);
            all.AddRange(next.Results[0].References);
            continuation = next.Results[0].ContinuationPoint;
        }
        return all;
    }

    public NodeId ToNodeId(ExpandedNodeId id) => ExpandedNodeId.ToNodeId(id, Session.NamespaceUris);

    /// <summary>Reads a method's InputArguments or OutputArguments property; empty when it has none.</summary>
    public async Task<IReadOnlyList<Argument>> GetMethodArgumentsAsync(NodeId method, string propertyName, CancellationToken ct)
    {
        foreach (var reference in await BrowseAsync(method, ct).ConfigureAwait(false))
        {
            if (reference.BrowseName.Name != propertyName)
                continue;
            var value = await Session.ReadValueAsync(ToNodeId(reference.NodeId), ct).ConfigureAwait(false);
            return value.Value is ExtensionObject[] items
                ? items.Select(e => e.Body).OfType<Argument>().ToList()
                : [];
        }
        return [];
    }

    public static string DescribeArgument(Argument a) =>
        $"{a.Name}: {TypeInfo.GetBuiltInType(a.DataType)}{(a.ValueRank >= 0 ? "[]" : string.Empty)}";

    /// <summary>Converts command-line text to the argument's declared built-in type.</summary>
    public static object ConvertArgument(string text, Argument a) => TypeInfo.GetBuiltInType(a.DataType) switch
    {
        BuiltInType.Boolean => bool.Parse(text),
        BuiltInType.SByte => sbyte.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.Byte => byte.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.Int16 => short.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.UInt16 => ushort.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.Int32 => int.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.UInt32 => uint.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.Int64 => long.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.UInt64 => ulong.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.Float => float.Parse(text, CultureInfo.InvariantCulture),
        BuiltInType.Double => double.Parse(text, CultureInfo.InvariantCulture),
        _ => text,
    };

    public static string Format(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        Array a => "[" + string.Join(", ", a.Cast<object?>().Select(Format)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public async ValueTask DisposeAsync()
    {
        await Session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        Session.Dispose();
    }
}
