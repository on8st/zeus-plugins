// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
//
// Run a fake Wavelog on loopback, for driving the plugin by hand without
// touching a real instance:   dotnet run --project tools/FakeWavelog -- 8099
using FakeWavelog;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8099;
using var server = new FakeWavelogServer(port) { ApiKey = "test-key" };
server.Start();

Console.WriteLine($"fake wavelog on {server.BaseUrl}   api key: {server.ApiKey}");
Console.WriteLine("endpoints: /index.php/api/{qso,get_contacts_adif,station_info,radio}");
Console.WriteLine("ctrl-c to stop");
Thread.Sleep(Timeout.Infinite);
