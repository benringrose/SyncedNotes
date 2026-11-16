// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// Based on Helpers/SettingsManager.cs from the PowerToys Command Palette extension template.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SyncedNotes.CmdPal.Helpers;

/// <summary>What happens after a note was saved from the "New note" page.</summary>
internal enum AfterSaveBehavior
{
    /// <summary>Close Command Palette and show a toast (fastest capture flow).</summary>
    Close,

    /// <summary>Keep the palette open and offer to open the new note.</summary>
    StayOpen,

    /// <summary>Open the new note in the default browser.</summary>
    OpenNote,
}

/// <summary>A validated server URL + API token pair.</summary>
/// <param name="BaseUrl">Absolute http(s) URL without a trailing slash, e.g. https://notes.example.com.</param>
/// <param name="Token">The bearer token (never log this).</param>
internal sealed record ServerConnection(string BaseUrl, string Token);

/// <summary>
/// Extension settings, persisted by the toolkit's <see cref="JsonSettingsManager"/> to a
/// settings.json file in the extension's per-user app data folder. They are edited on the
/// page that Command Palette shows for this extension (Settings → Extensions → SyncedNotes),
/// or through <see cref="Settings"/>.<c>SettingsPage</c>.
/// </summary>
internal sealed partial class SettingsManager : JsonSettingsManager
{
    private const string AfterSaveClose = "close";
    private const string AfterSaveStay = "stay";
    private const string AfterSaveOpen = "open";

    private static readonly string _namespace = "SyncedNotes.CmdPal";

    private static readonly SearchValues<char> _base64UrlChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static readonly List<ChoiceSetSetting.Choice> _afterSaveChoices =
    [
        new ChoiceSetSetting.Choice("Close Command Palette and show a confirmation", AfterSaveClose),
        new ChoiceSetSetting.Choice("Keep Command Palette open and offer to open the note", AfterSaveStay),
        new ChoiceSetSetting.Choice("Open the new note in the browser", AfterSaveOpen),
    ];

    // Note: the toolkit renders a TextSetting's *description* as the visible label of the input.
    private readonly TextSetting _serverUrl = new(
        Namespaced(nameof(ServerUrl)),
        "Server URL",
        "Server URL: the address you open SyncedNotes at, for example https://notes.example.com",
        string.Empty)
    {
        Placeholder = "https://notes.example.com",
    };

    private readonly SecretTextSetting _apiToken = new(
        Namespaced(nameof(ApiToken)),
        "API token",
        "API token: create one in SyncedNotes under Settings → Integrations (it starts with snp_)",
        string.Empty)
    {
        Placeholder = "snp_…",
    };

    private readonly ChoiceSetSetting _afterSave = new(
        Namespaced(nameof(AfterSave)),
        "After saving a note",
        "After saving a note from “New note”",
        _afterSaveChoices);

    public SettingsManager()
    {
        FilePath = SettingsJsonPath();

        Settings.Add(_serverUrl);
        Settings.Add(_apiToken);
        Settings.Add(_afterSave);

        // Load settings from file upon initialization
        LoadSettings();

        Settings.SettingsChanged += (_, _) => SaveSettings();
    }

    /// <summary>Gets the normalized server URL, or an empty string when it is missing or invalid.</summary>
    public string ServerUrl => TryNormalizeServerUrl(_serverUrl.Value, out var url) ? url : string.Empty;

    /// <summary>Gets the normalized API token (secret: never log or display it).</summary>
    public string ApiToken => NormalizeToken(_apiToken.Value);

    public AfterSaveBehavior AfterSave => _afterSave.Value switch
    {
        AfterSaveStay => AfterSaveBehavior.StayOpen,
        AfterSaveOpen => AfterSaveBehavior.OpenNote,
        _ => AfterSaveBehavior.Close,
    };

    public bool IsConfigured => TryGetConnection(out _, out _);

    /// <summary>Gets a value indicating whether the saved token has the shape SyncedNotes issues (snp_ + 43 base64url chars).</summary>
    public bool TokenLooksValid
    {
        get
        {
            var token = ApiToken;
            return token.Length == 47
                && token.StartsWith("snp_", StringComparison.Ordinal)
                && !token.AsSpan(4).ContainsAnyExcept(_base64UrlChars);
        }
    }

    /// <summary>
    /// Validates the configured server URL and API token.
    /// </summary>
    /// <param name="connection">The connection details when valid.</param>
    /// <param name="problem">A user-facing explanation of what is missing or wrong.</param>
    /// <returns><see langword="true"/> when both settings are usable.</returns>
    public bool TryGetConnection([NotNullWhen(true)] out ServerConnection? connection, [NotNullWhen(false)] out string? problem)
    {
        connection = null;

        var rawUrl = _serverUrl.Value?.Trim() ?? string.Empty;
        if (rawUrl.Length == 0)
        {
            problem = "Enter the address of your SyncedNotes server (for example https://notes.example.com) in the extension settings.";
            return false;
        }

        if (!TryNormalizeServerUrl(rawUrl, out var baseUrl))
        {
            problem = $"“{TextHelpers.Ellipsize(rawUrl, 60)}” isn't a valid server URL. Use the address you open SyncedNotes at, for example https://notes.example.com.";
            return false;
        }

        var token = ApiToken;
        if (token.Length == 0)
        {
            problem = "Enter an API token in the extension settings. Create one in SyncedNotes under Settings → Integrations.";
            return false;
        }

        if (!IsValidHeaderToken(token))
        {
            problem = "The API token contains spaces or other invalid characters. Copy it again from SyncedNotes (Settings → Integrations).";
            return false;
        }

        connection = new ServerConnection(baseUrl, token);
        problem = null;
        return true;
    }

    public override void SaveSettings()
    {
        // Store what we actually use: trimmed token without a pasted "Bearer " prefix,
        // and the URL without trailing slashes / fragments (only when it is valid, so the
        // user can still see and fix an invalid value).
        _apiToken.Value = NormalizeToken(_apiToken.Value);
        if (TryNormalizeServerUrl(_serverUrl.Value, out var url))
        {
            _serverUrl.Value = url;
        }

        base.SaveSettings();
    }

    /// <summary>
    /// Normalizes a user-entered server URL: adds https:// when no scheme is given, and drops
    /// any query/fragment (e.g. a pasted "https://host/#/note/…") and trailing slashes.
    /// </summary>
    internal static bool TryNormalizeServerUrl(string? raw, [NotNullWhen(true)] out string? baseUrl)
    {
        baseUrl = null;
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    internal static string NormalizeToken(string? raw)
    {
        const string bearerPrefix = "Bearer ";

        var token = (raw ?? string.Empty).Trim().Trim('"', '\'').Trim();
        if (token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = token[bearerPrefix.Length..].Trim();
        }

        return token;
    }

    internal static string SettingsJsonPath()
    {
        var directory = Utilities.BaseSettingsPath("SyncedNotes.CmdPal");
        Directory.CreateDirectory(directory);

        return Path.Combine(directory, "settings.json");
    }

    private static string Namespaced(string propertyName) => $"{_namespace}.{propertyName}";

    // Visible ASCII only: anything else would be rejected by (or could be injected into) the HTTP header.
    private static bool IsValidHeaderToken(string token)
    {
        foreach (var c in token)
        {
            if (c <= ' ' || c > '~')
            {
                return false;
            }
        }

        return true;
    }
}
