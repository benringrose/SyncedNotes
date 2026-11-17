using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SyncedNotes.CmdPal.Helpers;

/// <summary>
/// A <see cref="TextSetting"/> that the settings page renders as a password box
/// (Adaptive Cards <c>Input.Text</c> with <c>"style": "password"</c>), so the API token
/// isn't displayed in clear text. Storage and behaviour are identical to <see cref="TextSetting"/>.
/// </summary>
internal sealed class SecretTextSetting : TextSetting
{
    public SecretTextSetting(string key, string label, string description, string defaultValue)
        : base(key, label, description, defaultValue)
    {
    }

    public override Dictionary<string, object> ToDictionary()
    {
        var dictionary = base.ToDictionary();
        dictionary["style"] = "password";
        return dictionary;
    }
}
