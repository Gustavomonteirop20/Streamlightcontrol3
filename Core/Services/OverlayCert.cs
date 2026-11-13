using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Eigen certificaat voor de kerst-overlays, zodat TikTok LIVE Studio een https-adres met een echte domeinnaam krijgt
/// (https://kerst.slc-overlay.nl:7532/…). Werkt zoals mkcert: een eigen basiscertificaat (alleen op deze pc vertrouwd)
/// ondertekent het certificaat voor de domeinnaam. Bewaard in de instellingenmap, map overlay-cert.
/// </summary>
public sealed class OverlayCert
{
    public const string Host = "kerst.slc-overlay.nl";
    private const string CaName = "CN=Stream Light Control - kerst-overlays (alleen deze pc)";

    private readonly string _dir;
    public X509Certificate2 Ca { get; }
    public X509Certificate2 Leaf { get; }

    private OverlayCert(string dir, X509Certificate2 ca, X509Certificate2 leaf) { _dir = dir; Ca = ca; Leaf = leaf; }

    /// <summary>Laadt het certificaat, of maakt een nieuw als het ontbreekt, kapot is of bijna verloopt.</summary>
    public static OverlayCert LoadOrCreate(string dataDirectory)
    {
        var dir = Path.Combine(dataDirectory, "overlay-cert");
        Directory.CreateDirectory(dir);
        var caPath = Path.Combine(dir, "ca.pfx");
        var leafPath = Path.Combine(dir, "kerst.pfx");
        try
        {
            if (File.Exists(caPath) && File.Exists(leafPath))
            {
                var ca = Load(caPath);
                var leaf = Load(leafPath);
                if (leaf.NotAfter > DateTime.Now.AddDays(30) && ca.NotAfter > DateTime.Now.AddDays(30) && leaf.Issuer == ca.Subject)
                    return new OverlayCert(dir, ca, leaf);
            }
        }
        catch { /* opnieuw maken */ }

        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest(CaName, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var caCert = caReq.CreateSelfSigned(now, now.AddYears(10));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=" + Host, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Host);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        leafReq.CertificateExtensions.Add(san.Build());
        leafReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafReq.PublicKey, false));
        leafReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, true, false));
        var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7F;
        using var leafPub = leafReq.Create(caCert, now, now.AddDays(800), serial);
        using var leafCert = leafPub.CopyWithPrivateKey(leafKey);

        File.WriteAllBytes(caPath, caCert.Export(X509ContentType.Pfx));
        File.WriteAllBytes(leafPath, leafCert.Export(X509ContentType.Pfx));
        File.WriteAllBytes(Path.Combine(dir, "ca.cer"), caCert.Export(X509ContentType.Cert));
        return new OverlayCert(dir, Load(caPath), Load(leafPath));
    }

    // Via een pfx-bestand laden: zo kan Windows (SChannel) de sleutel gebruiken voor https
    private static X509Certificate2 Load(string path) =>
        new(File.ReadAllBytes(path), (string?)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

    /// <summary>Staat het basiscertificaat bij "Vertrouwde basiscertificeringsinstanties" van deze gebruiker?</summary>
    public bool IsTrusted()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, Ca.Thumbprint, false).Count > 0;
        }
        catch { return false; }
    }

    /// <summary>Basiscertificaat vertrouwen (Windows vraagt de gebruiker om toestemming). Alleen het publieke deel.</summary>
    public void Trust()
    {
        using var pub = new X509Certificate2(Ca.Export(X509ContentType.Cert));
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(pub);
    }

    /// <summary>Wijst de domeinnaam op deze pc naar de app (hosts-bestand)?</summary>
    public static bool HostPointsHere()
    {
        try
        {
            var t = Dns.GetHostAddressesAsync(Host);
            return t.Wait(2000) && t.Result.Any(IPAddress.IsLoopback);
        }
        catch { return false; }
    }

    public string CaFile => Path.Combine(_dir, "ca.cer");
}
