# UberSDR — design notes

Scope set by the operator: **receive only**, against the **public** instance
network. Transport was left to me. This records what was verified and what it
rules out, because the verification changed the answer.

## 1. What was checked, and how

UberSDR is a `ka9q-radio`-based platform (RX-888 MkII + generic PC) with a public
receiver network. It advertises an unusual number of integration surfaces: REST
and WebSocket, an HPSDR bridge, KiwiSDR emulation on 8073, RTL-TCP on 1234, TCI,
rigctl/flrig, SoapySDR, MQTT.

That list is what made this look easy — Zeus is *itself* an OpenHPSDR client and
already ships TCI and a `KiwiSdrService`. Three surfaces Zeus speaks natively.

So the obvious plan was: point Zeus's existing KiwiSDR client at an UberSDR
instance on 8073 and write no transport code at all.

**The directory API says otherwise.** `GET https://instances.ubersdr.org/api/instances`
is public, unauthenticated JSON — 54 instances, 53 online at the time of
checking. Across all of them:

| | |
|---|---|
| Ports advertised | `443` ×49, `80` ×2, `9080`, `8080`, `9443` |
| Advertising **8073** | **none** |
| TLS | 50 of 54 |
| `cors_enabled` | **false on all 54** |
| `public_iq_modes` | every instance offers at least `iq48` |

Public access is UberSDR's own HTTPS/WebSocket protocol through
`*.tunnel.ubersdr.org` or an operator's own host. The KiwiSDR emulation is
documented against `ubersdr.local` — mDNS, local network — and **no public
instance exposes it**. So the free ride does not exist.

## 2. What that rules out

**Audio from a public UberSDR instance cannot reach Zeus's receive chain.** Three
independent reasons, any one sufficient:

1. The only public transport is UberSDR's own WebSocket protocol. Zeus does not
   speak it.
2. No public instance exposes a transport Zeus *does* speak — no 8073, no raw
   HPSDR, no TCI.
3. **A plugin cannot be a receiver source.** `IAudioPlugin` processes an insert
   chain, input to output. `IRxAudioTapPlugin` is explicitly *"a read-only,
   non-destructive tap"* that *"produces no output"*. Nothing in the contracts
   introduces audio or IQ into the RX path; radio sources are engine-level.

Point (3) is the one that matters, because it holds even if a transport existed.
Making Zeus listen to UberSDR is an **engine** feature — a client for
`iq48` alongside the existing HPSDR and Kiwi services — not something a plugin
can do. If that is the real goal, the route is a feature request upstream, not
this repository.

## 3. What is actually worth building

The directory API is rich, and the interesting content is not audio:

```
callsign, name, location, lat/lon, maidenhead, country
distance, bearing_degrees            ← relative to the caller
snr_0_30_mhz, snr_1_8_30_mhz, noise_floor
digital_decodes, cw_skimmer, dsp_enabled, tdoa_enabled
max_clients, available_clients, peak_users, is_online, load_status
public_url, host, port, tls
pskreporter_rank
```

plus `/api/ionosonde/mufd.geojson` and `/api/ionosonde/stations.json` for
propagation.

**Proposal: a receiver-conditions panel, not a receiver.**

What makes it a Zeus plugin rather than a browser bookmark is the one thing
`IPluginContext` *does* give us: `Radio.FrequencyChanged` and `Band`. The panel
knows what band the operator is on and answers *"who near me is hearing this
band right now, and how well"* — sorted by distance, filtered to instances with
capacity, showing SNR and noise floor. One click opens the receiver in a browser
for actual listening.

That is honest about the limit rather than working around it: Zeus stays the
radio, UberSDR stays the wide-area ears, and the plugin is the thing that
connects what you are doing to what is being heard elsewhere.

**The fetching must happen in the backend, not the panel.** `cors_enabled` is
false on every instance, so browser-side requests to per-instance APIs are
blocked. `IBackendPlugin` routes have no such restriction — a point in favour of
the split the framework already encourages.

## 4. Open questions

1. Is the receiver-conditions framing what is wanted, or was in-Zeus listening
   the point? If the latter, this belongs upstream as an engine feature and this
   directory should be closed.
2. Directory only, or per-instance data too — the richer fields (spectrum,
   decodes) come from each instance's own API, 54 different hosts, none
   CORS-enabled and each with its own load limits.
3. Politeness: `/api/instances` is 638 KB. Cache it, poll it slowly, and honour
   `available_clients` before suggesting a receiver.

## 5. Not verified

- Whether per-instance APIs need auth, and what they expose.
- Whether the directory API is intended for third-party use or merely happens to
  be reachable. **Ask before shipping anything that polls it.**
