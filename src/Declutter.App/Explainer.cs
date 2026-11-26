using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Declutter.Core.Explain;
using Declutter.Core.Reports;

namespace Declutter.App;

/// <summary>
/// Optional "Explain" answers from Claude, using the user's own Anthropic API key. Off until a key is added in
/// Settings; the key is encrypted for this Windows account (DPAPI). Only the one item clicked is sent, with the
/// user's name and profile path removed.
/// </summary>
public sealed class Explainer(AppServices services)
{
    public const string Model = "claude-opus-5-5";
    private static readonly byte[] Entropy = "Declutter.ExplainKey.v1"u8.ToArray();

    /// <summary>Set at start-up so list rows can offer Explain without passing services around.</summary>
    public static Explainer? Shared { get; set; }

    public bool IsConfigured => LoadKey() != null;

    public void SaveKey(string? key)
    {
        key = key?.Trim();
        string? sealedKey = string.IsNullOrEmpty(key) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
        services.UpdateSettings(s => s with { ProtectedApiKey = sealedKey });
        services.Log.Write("Explain", sealedKey == null ? "API key removed" : "API key saved");
    }

    private string? LoadKey()
    {
        if (services.Settings.ProtectedApiKey is not { Length: > 0 } stored) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // copied from another PC or account: ask for the key again
        }
    }

    /// <summary>Exactly what would be sent for this item (shown in the window, so nothing is hidden).</summary>
    public string Describe(ExplainRequest request) =>
        ExplainPrompt.BuildUserMessage(request, new ReportBuilder(services.Folders.UserProfile, Environment.UserName).Redact);

    /// <summary>Opens the explanation window for one item.</summary>
    public void Show(ExplainRequest request)
    {
        var window = new ExplainWindow(this, request);
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.Show();
    }

    /// <summary>Asks Claude; returns the answer, or throws <see cref="ExplainException"/> with a friendly message.</summary>
    public async Task<string> AskAsync(ExplainRequest request, CancellationToken ct)
    {
        string key = LoadKey() ?? throw new ExplainException("Add your Anthropic API key in Settings first.");
        var client = new AnthropicClient { ApiKey = key };
        try
        {
            BetaMessage response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                System = ExplainPrompt.System,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                // If Claude declines (rare false positives on security topics), the API answers with its fallback model.
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
                Messages = [new BetaUserMessageParam { Content = Describe(request) }],
            }, ct);

            if (response.StopReason == "refusal")
                throw new ExplainException("Claude didn't answer this one. Try searching for the name online instead.");
            string text = string.Join("\n\n", response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text.Trim()).Where(t => t.Length > 0));
            services.Log.Write("Explain", $"Explained \"{request.Name}\" ({request.Kind})");
            return text.Length > 0 ? text : throw new ExplainException("Claude sent an empty answer. Please try again.");
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new ExplainException("Anthropic didn't accept the API key. Check it in Settings.");
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new ExplainException("This API key isn't allowed to use Claude: " + ex.Message);
        }
        catch (AnthropicRateLimitException)
        {
            throw new ExplainException("Too many requests right now. Wait a minute and try again.");
        }
        catch (AnthropicApiException ex)
        {
            throw new ExplainException("Claude couldn't answer: " + ex.Message);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or AnthropicIOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ExplainException("Couldn't reach Anthropic. Check your internet connection.");
        }
    }
}

public sealed class ExplainException(string message) : Exception(message);
