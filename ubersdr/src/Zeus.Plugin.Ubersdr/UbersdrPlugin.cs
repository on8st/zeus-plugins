// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Ubersdr;

/// <summary>
/// Placeholder entrypoint so the scaffold builds and loads. Does nothing yet.
/// </summary>
public sealed class UbersdrPlugin : IZeusPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        context.Logger?.LogInformation("ubersdr: loaded (scaffold — does nothing yet)");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
