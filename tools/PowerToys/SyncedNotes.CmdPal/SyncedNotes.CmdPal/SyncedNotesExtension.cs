// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// Based on the PowerToys Command Palette extension template.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace SyncedNotes.CmdPal;

// This CLSID must match both <com:Class Id> and <CreateInstance ClassId> in Package.appxmanifest.
[Guid("30A44282-4941-4E0D-98C5-A4B06AC9FD41")]
public sealed partial class SyncedNotesExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;

    private readonly SyncedNotesCommandsProvider _provider = new();

    public SyncedNotesExtension(ManualResetEvent extensionDisposedEvent)
    {
        this._extensionDisposedEvent = extensionDisposedEvent;
    }

    public object? GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => _provider,
            _ => null,
        };
    }

    public void Dispose()
    {
        _provider.Dispose();
        this._extensionDisposedEvent.Set();
    }
}
