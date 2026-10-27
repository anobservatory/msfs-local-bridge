using System.Security.Cryptography.X509Certificates;
using static BridgePaths;

internal static class BridgeTls
{
  internal static X509KeyStorageFlags GetTlsCertificateKeyStorageFlags()
  {
    return OperatingSystem.IsWindows()
      ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
      : X509KeyStorageFlags.Exportable;
  }

  internal static X509Certificate2 LoadWssCertificate(BridgeOptions options)
  {
    if (options.HasPfxCertificate)
    {
      var pfxPath = ResolveRuntimePath(options.TlsPfxPath);
      if (!File.Exists(pfxPath))
      {
        throw new FileNotFoundException(
          $"WSS PKCS#12 certificate file not found. Set MSFS_BRIDGE_TLS_PFX_PATH or create file at '{pfxPath}'.",
          pfxPath
        );
      }

      try
      {
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, options.TlsPfxPassword, GetTlsCertificateKeyStorageFlags(), null);
      }
      catch (Exception ex)
      {
        throw new InvalidOperationException(
          $"Failed to load WSS PKCS#12 certificate from pfx='{pfxPath}'.",
          ex
        );
      }
    }

    var certPath = ResolveRuntimePath(options.TlsCertPath);
    var keyPath = ResolveRuntimePath(options.TlsKeyPath);

    if (!File.Exists(certPath))
    {
      throw new FileNotFoundException(
        $"WSS certificate file not found. Set MSFS_BRIDGE_TLS_CERT_PATH or create file at '{certPath}'.",
        certPath
      );
    }

    if (!File.Exists(keyPath))
    {
      throw new FileNotFoundException(
        $"WSS private key file not found. Set MSFS_BRIDGE_TLS_KEY_PATH or create file at '{keyPath}'.",
        keyPath
      );
    }

    try
    {
      // Kestrel expects a certificate with private key. PEM loading is normalized via PKCS#12 re-wrap.
      using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
      var pkcs12 = pem.Export(X509ContentType.Pkcs12, string.Empty);
      return X509CertificateLoader.LoadPkcs12(pkcs12, string.Empty, GetTlsCertificateKeyStorageFlags(), null);
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException(
        $"Failed to load WSS certificate from cert='{certPath}', key='{keyPath}'.",
        ex
      );
    }
  }
}
