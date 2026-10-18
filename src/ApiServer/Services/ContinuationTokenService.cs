using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ApiServer.Services;

/// <summary>Inhalt eines Fortsetzungs-Tokens. <c>Op</c> bindet es an eine Operation, <c>Bind</c> an Filter bzw. Content-Referenz.</summary>
public sealed record ContinuationPayload(string Op, string Bind, string? Url, int Offset, string? Version, long Exp);

/// <summary>
/// HMAC-SHA256-signierte, kurzlebige Fortsetzungs-Tokens (zustandslos, keine Speicherung). Schluessel: ausschliesslich der
/// dedizierte Schluessel <c>ResponseLimits:ContinuationSigningKey</c> (Azure: Key-Vault-Referenz, von Terraform erzeugt). Es gibt
/// keine Ableitung aus anderen Secrets. Nur in Development (und direkten Tests ohne Host) wird ohne Konfiguration ein zufaelliger
/// Prozess-Schluessel erzeugt; ausserhalb von Development verhindert <see cref="ResponseLimitsOptionsValidator"/> den Start.
/// Der Schluessel wird nie geloggt oder ausgegeben.
/// </summary>
public sealed class ContinuationTokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;

    public ContinuationTokenService(IOptions<ResponseLimitsOptions> options, TimeProvider? time = null)
    {
        var o = options.Value;
        _lifetime = TimeSpan.FromMinutes(Math.Clamp(o.ContinuationTokenMinutes, 1, 120));
        _time = time ?? TimeProvider.System;

        _key = ResponseLimitsOptionsValidator.IsValidKey(o.ContinuationSigningKey)
            ? HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(o.ContinuationSigningKey!), 32, info: Encoding.UTF8.GetBytes("m365-mcp/continuation-token/v1"))
            : RandomNumberGenerator.GetBytes(32);
    }

    public string Create(string op, string bind, string? url, int offset, string? version)
    {
        var payload = new ContinuationPayload(op, bind, url, offset, version, _time.GetUtcNow().Add(_lifetime).ToUnixTimeSeconds());
        var body = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return body + "." + Sign(body);
    }

    /// <summary>Prueft Signatur, Ablauf und Bindung. Fehler enthalten nie Token-Inhalt.</summary>
    public bool TryValidate(string? token, string op, string bind, out ContinuationPayload payload, out string error)
    {
        payload = default!;
        error = "invalid_continuation_token: Token ungueltig.";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(parts[0])), Encoding.ASCII.GetBytes(parts[1])))
        {
            return false;
        }

        try
        {
            var padded = parts[0].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            payload = JsonSerializer.Deserialize<ContinuationPayload>(Convert.FromBase64String(padded))!;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        if (payload is null || payload.Exp < _time.GetUtcNow().ToUnixTimeSeconds())
        {
            error = "invalid_continuation_token: Token abgelaufen; Abfrage neu starten.";
            return false;
        }

        if (payload.Op != op || payload.Bind != bind)
        {
            error = "invalid_continuation_token: Token passt nicht zu dieser Abfrage (andere Operation oder geaenderte Filter).";
            return false;
        }

        return true;
    }

    private string Sign(string body)
    {
        using var hmac = new HMACSHA256(_key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(body))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
