using VibeRails.DTOs;
using VibeRails.Services.SigningKeys;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// The key-and-password part shared by the Git Guard commit popup and the review approval popup:
/// wait for a synced signing key, choose one, read its password with echo off and sign a tree.
/// It decides nothing about what is signed or what happens to the signature; the popup does.
/// </summary>
/// <param name="waitHint">What the person does not need to repeat while this window waits for a key
/// ("the commit does not need to be started again").</param>
internal sealed class SigningKeyPrompt(
    TextWriter output,
    IHumanSignatureConsole console,
    ICommitTreeSigner signer,
    VcaConsoleStyle style,
    string waitHint)
{
    /// <summary>
    /// Lists the keys that can sign. With none synced, the window waits for the person to create and
    /// sync one in Settings &gt; KEYS and checks again on Enter, instead of failing and leaving a
    /// "no signing key" window behind while they set one up for a second attempt (VIBE-200).
    /// Null means cancelled, timed out or unreadable.
    /// </summary>
    public async Task<IReadOnlyList<SigningKeyInfo>?> WaitForSyncedKeysAsync(CancellationToken cancellationToken)
    {
        for (var check = 0; ; check++)
        {
            IReadOnlyList<SigningKeyInfo> keys;
            try
            {
                keys = await signer.ListKeysAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await output.WriteLineAsync($"{style.Red}Could not read your signing keys: {ex.Message}{style.Reset}");
                return null;
            }
            if (keys.Count > 0)
                return keys;

            if (check == 0)
            {
                await output.WriteLineAsync($"{style.Yellow}No signing key on this computer is synced to viberails.ai.{style.Reset}");
                await output.WriteLineAsync(
                    "Create one in VibeRails Settings > KEYS with your API key saved and sync it. This window waits; "
                    + waitHint + ".");
            }
            else
            {
                await output.WriteLineAsync($"{style.Yellow}Still no synced signing key.{style.Reset}");
            }

            if (await console.ReadLineAsync("Press Enter to check again (Esc cancels): ", cancellationToken) is null)
            {
                await output.WriteLineAsync("Cancelled. Nothing was signed.");
                return null;
            }
        }
    }

    public async Task<SigningKeyInfo?> ChooseKeyAsync(IReadOnlyList<SigningKeyInfo> keys, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Signing keys synced to viberails.ai:");
        for (var i = 0; i < keys.Count; i++)
            await output.WriteLineAsync($"  {i + 1}) {keys[i].Name}  fingerprint {keys[i].Fingerprint[..12]}");
        if (keys.Count == 1)
            return keys[0];

        while (true)
        {
            var answer = await console.ReadLineAsync($"Key number [1-{keys.Count}, Enter for 1, Esc cancels]: ", cancellationToken);
            if (answer is null) return null;
            answer = answer.Trim();
            if (answer.Length == 0) return keys[0];
            if (int.TryParse(answer, out var number) && number >= 1 && number <= keys.Count)
                return keys[number - 1];
            await output.WriteLineAsync($"Enter a number from 1 to {keys.Count}.");
        }
    }

    /// <summary>Up to <see cref="HumanSignaturePopup.MaxPasswordAttempts"/> passwords; only a wrong password earns another try.</summary>
    public async Task<CommitSignature?> SignWithPasswordAsync(SigningKeyInfo key, string treeId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= HumanSignaturePopup.MaxPasswordAttempts; attempt++)
        {
            var password = await console.ReadSecretAsync($"Password for \"{key.Name}\" (hidden, Esc cancels): ", cancellationToken);
            if (password is null)
            {
                await output.WriteLineAsync("Cancelled. Nothing was signed.");
                return null;
            }

            try
            {
                return await signer.SignAsync(key.Id, password, treeId, cancellationToken);
            }
            catch (SigningKeyLockedException ex)
            {
                await output.WriteLineAsync($"{style.Red}{ex.Message}{style.Reset}");
                return null;
            }
            catch (ArgumentException ex)
            {
                await output.WriteLineAsync($"{style.Red}{ex.Message}{style.Reset}");
                // Only a wrong password is worth another try; every other refusal repeats itself.
                if (!ex.Message.StartsWith("Incorrect password", StringComparison.Ordinal)
                    && !ex.Message.StartsWith("Password ", StringComparison.Ordinal))
                    return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException
                or System.Text.Json.JsonException)
            {
                await output.WriteLineAsync($"{style.Red}Could not use the signing key: {ex.Message}{style.Reset}");
                return null;
            }
        }

        await output.WriteLineAsync("Too many incorrect passwords. Nothing was signed.");
        return null;
    }
}
