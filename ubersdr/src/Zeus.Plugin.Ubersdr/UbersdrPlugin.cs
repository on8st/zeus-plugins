// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugin.Ubersdr;

/// <summary>
/// Probe: reports which host services this plugin is actually handed.
///
/// <para>The contracts declare <c>IRadioStateReader</c>, <c>IRadioController</c>
/// and <c>IAudioPlaybackSink</c>, but the published engine source contains no
/// implementation of any of them — each is a <c>GetService&lt;T&gt;()</c> lookup
/// that returns null unless something registers it. Whether the shipped engine
/// registers them cannot be read from the source, so it is asked at runtime.</para>
///
/// <para>Everything the remote monitor needs rests on the answer.</para>
/// </summary>
public sealed class UbersdrPlugin : IZeusPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        var log = context.Logger;

        log.LogWarning("ubersdr-probe: Radio            = {V}", Describe(context.Radio));
        log.LogWarning("ubersdr-probe: RadioController  = {V}", Describe(context.RadioController));
        log.LogWarning("ubersdr-probe: Playback         = {V}", Describe(context.Playback));
        log.LogWarning("ubersdr-probe: Qrz              = {V}", Describe(context.Qrz));
        log.LogWarning("ubersdr-probe: OperatorIdentity = {V}", Describe(context.OperatorIdentity));

        if (context.Radio is { } radio)
        {
            log.LogWarning("ubersdr-probe: freq={Hz} Hz mode={Mode} band={Band} mox={Mox}",
                radio.FrequencyHz, radio.Mode, radio.Band, radio.Mox);

            // What FrequencyHz reports under split is the second open question,
            // and it can only be answered by watching it while the operator
            // moves the VFOs.
            radio.FrequencyChanged += hz => log.LogWarning("ubersdr-probe: freq -> {Hz}", hz);
            radio.ModeChanged += m => log.LogWarning("ubersdr-probe: mode -> {Mode}", m);
            radio.MoxChanged += on => log.LogWarning("ubersdr-probe: MOX  -> {On}", on);
        }

        if (context.Playback is { } sink)
            log.LogWarning("ubersdr-probe: playback backlog={Backlog} moxOn={Mox}",
                sink.LocalMonitorBacklog, sink.IsMoxOn);

        return Task.CompletedTask;
    }

    private static string Describe(object? service) =>
        service is null ? "NULL — not provided by this host" : service.GetType().FullName ?? "present";

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
