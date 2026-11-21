# Cheater or Not

Cheater or Not is a Windows desktop evidence-review tool for Counter-Strike 2. It
helps a reviewer collect public player statistics, preserve the source and sample
limitations behind those statistics, and inspect locally recorded demo footage.

It was created to organize evidence rather than turn statistics into accusations.
Different websites can measure different matches, metrics, and time windows, so
the app keeps their samples separate instead of treating them as one combined
truth.

## Important limitations

**This is not an anti-cheat system and cannot prove cheating or clean play.**

- A statistical flag is only a reason to review more evidence.
- No flag, missing data, or an AI response is not proof that a player is clean.
- Video is reconstructed from a demo; it is not original player footage and does
  not recover mouse input, full player knowledge, intent, or exact visibility.
- Gemini output is unverified commentary, not an accusation, verdict, or ban
  recommendation.
- Do not use a report alone to harass, report, or punish anyone.

## What the app does

### Public player checks

The app can collect supported public statistics and optional official API data for
one or more SteamID64 values. Results show:

- The source and URL for each sample
- Sample sizes and source synchronization times
- Repeated metrics by source, without averaging incompatible samples
- Rolling CSTracker periods and their denominators
- Rank, medal, ban, utility, behavior, and account context
- Screening-rule matches and the measurements that triggered them
- Missing, private, blocked, or unavailable data instead of assuming zero

Each player remains a separate result. Multiple players can be checked in one
batch by separating Steam IDs or profile URLs with spaces or commas.

### Local demo review

An uncompressed Source 2 CS2 `.dem` can be imported locally without contacting a
website or uploading the recording. The app can display:

- The recorded human roster and SteamIDs
- Death-event bookmarks
- Experimental shot/view-direction measurements
- Round and combat telemetry
- Engagement and geometry context

From that local report, you can:

- Select **Use roster for checks** and then check those public profiles.
- Capture one attacker-POV clip for every eligible human-vs-human kill.
- Prepare bounded video batches for optional Gemini review.
- Save or copy the local report.

Capture requires Steam and CS2 to be installed and CS2 to be closed. It uses
offline reconstruction, can take more than 20 minutes for a large match, and
temporarily requires substantial free disk space.

## Install and start

Download the portable Windows x64 build from the
[Releases page](https://github.com/ShAgGy2035/CheaterOrNot/releases). No
installer or separate .NET install is needed.

1. Download `CheaterOrNot-v0.0.1-win-x64.zip` (or the latest version) from the
   newest release.
2. Extract the complete zip to a trusted local folder.
3. Run `CheaterOrNot.exe`.
4. If Windows displays an unsigned-application warning, review the warning and
   only continue if you trust the download.

The app includes its .NET runtime and browser support. Keep the extracted files
together.

### Build from source (optional)

Requires the .NET SDK:

```powershell
dotnet publish src\CheaterOrNot\CheaterOrNot.csproj -c Release -r win-x64 -p:PublishProfile=Portable -o publish
```

The result is `publish\CheaterOrNot.exe`.

### Capture components

FFmpeg, CS Demo Manager, and the extractor are not redistributed in the public
application folder. On first capture, the app automatically downloads
size-verified and SHA-256-verified upstream releases into its local `tools`
folder. It keeps the upstream licenses and records the release provenance.

Do not redistribute that configured `tools` folder or anything under
`tools/data`. See [capture-tool-notices.txt](src/CheaterOrNot/Assets/capture-tool-notices.txt).

## Basic use: check a public player profile

1. Enter a SteamID64 or supported public profile URL in the main input box.
2. Select **Check player**.
3. Wait for the progress bar and source status updates to finish.
4. Review the report:
   - **Final result** contains the readable summary and any screening flags.
   - **Evidence** contains source values, samples, timestamps, cautions, and
     calculation details.
   - **Screening rules** explains the current thresholds. These rules are
     screening heuristics, not validated cheating criteria.
5. Select **Copy all** on either tab or **Save report** to export the complete
   Final result and Evidence report.

For a batch, enter several IDs or URLs separated by spaces or commas before
pressing **Check player**. Each remains an independent source review.

### Check player with AI

**Check player with AI** first performs the normal public-data check. It then
sends the assessed, identity-stripped statistics to Gemini for a separately
written narrative review.

This requires a saved Gemini key. Names and SteamIDs are not included in the
website-statistics AI request. If Gemini fails, the normal local calculation
remains available as the audit baseline. **Retry AI review** uses the retained
website snapshot without repeating the website checks.

## Basic use: inspect a local demo

### Local import only

1. Select **Import local CS2 demo**.
2. Choose an uncompressed Source 2 `.dem` file.
3. Wait for local parsing to finish.
4. Review the demo on the **Final result** and **Evidence** tabs.
5. In Evidence, optionally select **Use roster for checks** to populate the main
   player field. You must still press **Check player** before any public
   website/API request is made.

Importing does not modify or upload the demo. It does not contact Steam,
statistic websites, Gemini, or optional APIs.

### Capture local review clips

1. Import the demo.
2. Select **Advanced video tools**.
3. Review the planned clips and select **Capture review clips locally**.
4. Close CS2 and any other running game session first.
5. Wait for recording and encoding to finish.
6. Use **Open output folder** to inspect the retained clips and manifest.

Capture does not send footage anywhere. X-ray and player voices are requested
off; game footage, HUD text, names, avatars, and game audio may still be
recorded. Treat the capture files as private.

## Basic use: analyze a demo with Gemini

This workflow uploads prepared video and bounded telemetry to Google.

1. Save a Gemini API key under **API Keys**.
2. Close CS2.
3. Select **Analyze demo with Gemini**.
4. Select the local `.dem` file.
5. The app imports the demo, captures/reuses verified clips, prepares local
   review copies, and displays progress.
6. Review the request/clip count and limits.
7. Selecting **Analyze demo with Gemini** is the upload authorization; the
   prepared batches are then sent automatically.

The advanced path is available through **Advanced video tools** and
**Preview Gemini video review**. There, selecting **Send all prepared batches
to Gemini** authorizes upload.

Large reviews are split into bounded requests. Accepted batches are checkpointed
by their exact request hash, so a later failure can resume without re-uploading
the accepted batches. Transient API and browser failures receive up to three
total attempts.

### Where the AI result appears

- **Final result** shows one concise card per player, a short set of
  representative observations, and up to three proposed review moments.
- **Evidence** retains full observations, all batch details, source hashes,
  limitations, and the combined detailed review.

“Review suggested” means only that Gemini proposed a specific moment for manual
inspection. It is not a locally corroborated suspicion, cheating probability, or
diagnosis.

## API keys and optional services

Open **API Keys** to configure optional integrations:

| Integration | What it adds | Network destination |
|---|---|---|
| Steam Web API | Public account, ban, and available lifetime context | `api.steampowered.com` |
| Leetify Public CS API | Official public-profile and match context | `api-public.cs-prod.leetify.com` |
| FACEIT Data API | Identity-verified FACEIT CS2 lifetime context | `open.faceit.com` |
| Gemini | Website-statistics commentary or demo video review | `generativelanguage.googleapis.com` |

Saving a key stores it locally; saving alone does not send it. Use each service's
**Test** button to check a saved key. Provider accounts, permissions, quotas,
charges, and retention terms still apply.

The normal public-website check does not require an API key. Missing provider
keys or unavailable optional services do not turn their data into zero.

## How to interpret a report

Use reports as a workflow:

1. **Confirm identity and sample scope.**
   Check the SteamID, provider, sample size, mode, and freshness.
2. **Read the Evidence, not only the summary.**
   A repeated-looking metric from another site may represent a different sample.
3. **Look for ordinary explanations.**
   Matchmaking rank, opponent strength, roles, maps, weapon choice, sample
   selection, and legitimate improvement can affect statistics.
4. **Review local clips manually.**
   Verify camera attribution, visibility, timing, and whether an observation
   is actually discernible. Demo reconstruction can be imperfect.
5. **Treat missing data as missing.**
   A failed or private source is not evidence of either cheating or clean play.

## Privacy and data sent over the network

- Local demo import is offline.
- Local capture is offline until you explicitly authorize Gemini upload.
- Public player checks request the entered SteamID/profile from supported
  websites and configured APIs.
- Browser checks use disposable anonymous Chromium sessions.
- API keys are stored through protected local storage and excluded from reports.
- **Check player with AI** sends assessed statistics without names or SteamIDs.
- **Analyze demo with Gemini** or **Send all prepared batches** sends
  identifiable footage, game audio that may be present, and bounded telemetry.
- Raw `.dem` files are not uploaded.

Google processing, retention, free-tier data use, quotas, and possible charges
are governed by Google's current terms. The app cannot retract data after it
has been delivered.

## Troubleshooting

### A website times out or fails

The app makes up to three total attempts for temporary timeouts, 429 responses,
network failures, and 5xx errors. It does not repeatedly retry permanent
responses such as rejected keys, missing/private profiles, or disallowed routes.
Other sources can still complete even when one source is unavailable.

If a public page repeatedly fails:

1. Confirm the SteamID/profile URL is correct.
2. Retry the check later.
3. Review the source-specific note in Evidence.
4. Do not infer anything from a failed source.

### Capture tools are missing

Start the capture action again. The app downloads verified missing components
automatically and shows progress in the main status area. Check network access
and disk space if setup fails.

### Capture cannot start

- Close CS2 completely.
- Ensure Steam and Counter-Strike 2 are installed.
- Ensure sufficient free space is available.
- Do not use the capture session for matchmaking.
- Check that the `.dem` is uncompressed and compatible with the current CS2
  demo format.

### Gemini fails

- Verify or retest the saved API key.
- Run **Gemini connection diagnostic**.
- Check provider quota and billing status.
- Resume the same retained preparation if available; accepted batch checkpoints
  are reused.
- Review the exact failure in the status and Evidence output.

## Development

Requirements:

- Windows x64
- .NET 10 SDK

Build and test:

```powershell
dotnet test CheaterOrNot.sln -c Release
dotnet publish src\CheaterOrNot\CheaterOrNot.csproj -c Release -r win-x64 -p:PublishProfile=Portable -o publish
```

Close the running published executable before replacing it.

Technical behavior, parser details, screening formulas, capture implementation,
and validation notes are maintained in
[docs/TECHNICAL-REFERENCE.md](docs/TECHNICAL-REFERENCE.md).

## Current project status

This is a development build. The intended release behavior is to distribute the
application and bundled browser/runtime notices, while obtaining capture
components from their upstream releases on the user's machine. Ordinary
development builds are not separate releases.
