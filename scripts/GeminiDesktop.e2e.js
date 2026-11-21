// Load into the Computer Use exec_js runtime after reading its documentation.
var geminiDesktopE2E = (() => {
    var app, demoPath, phase = "new", finalBefore, evidenceBefore, reportBefore, previewBefore, localRoster;
    function check(condition, message) {
        if (!condition) throw new Error(message);
    }
    function read() {
        return app.getAXState({ emit: false, disableDiffing: true, maxDepth: 10, maxChildren: 1000 });
    }
    function node(id, role) {
        var matches = read().split("\n").filter(line =>
            (!role || line.includes(role)) &&
            (line.includes(" ID: " + id + " ") || line.endsWith(" ID: " + id)));
        check(matches.length === 1, "Expected one visible control: " + id);
        return matches[0];
    }
    function index(line) {
        var match = line.match(/^\s*(\d+) AX/);
        check(match !== null, "Control has no accessible index.");
        return Number(match[1]);
    }
    function click(id, role) {
        var line = node(id, role);
        check(!line.includes("(disabled)"), "Control is disabled: " + id);
        app.click(index(line));
    }
    function value(id) {
        var line = node(id);
        var start = line.indexOf("Value: ");
        return start < 0 ? "" : line.slice(start + 7, line.lastIndexOf(" ID: " + id)).trim();
    }
    function tab(label) {
        var matches = read().split("\n").filter(line =>
            /AXTab \((selected|selectable)\)/.test(line) &&
            line.includes(") " + label + " screen@"));
        check(matches.length === 1, "Expected one visible tab: " + label);
        app.click(index(matches[0]));
    }
    function keys(object, expected) {
        check(Object.keys(object).sort().join(",") === expected.slice().sort().join(","),
            "The preview contains missing or unexpected fields.");
    }
    function selectWebsite() {
        var website = read().split("\n").find(line => line.includes(" ID: WebsiteReportButton") && !line.includes("(disabled)"));
        if (!website) return false;
        app.click(index(website));
        return true;
    }
    return {
        begin: function (target, authorizedDemoPath) {
            check(phase === "new", "Start a new driver instance for a new run.");
            check(typeof authorizedDemoPath === "string" && /\.dem$/i.test(authorizedDemoPath),
                "Supply an authorized local .dem path.");
            demoPath = authorizedDemoPath;
            app = cua.getApp(target, { maxDepth: 1 });
            check(node("CancelButton").includes("(disabled)"), "The app is busy; do not interrupt it.");
            if (selectWebsite()) {
                tab("Final result");
                finalBefore = value("FinalResultBox");
                tab("Evidence");
                evidenceBefore = value("ReportBox");
            }
            click("ImportDemoButton");
            phase = "picker";
            return "Native demo picker opened; no network submission.";
        },
        selectDemo: function () {
            check(phase === "picker", "Open the demo picker first.");
            var state = read();
            check(state.includes("AXWindow Import a local CS2 demo (no upload)"),
                "Expected the native local-demo file picker.");
            var field = state.split("\n").filter(line =>
                line.includes("AXTextField") && line.includes("File name:") && line.includes(" ID: 1148 "));
            check(field.length === 1, "Expected one native file-name field.");
            app.setValue(index(field[0]), demoPath);
            phase = "importing";
            click("1", "AXButton");
            return "Actual local demo selected through the native picker; import started.";
        },
        previewWhenReady: function () {
            check(phase === "importing", "A demo import must be in progress.");
            if (!node("CancelButton").includes("(disabled)"))
                return "WAIT: demo import is still running; observe again later.";
            tab("Evidence");
            reportBefore = value("ReportBox");
            click("VideoComponentsButton");
            click("PreviewGeminiButton");
            // AX value text flattens line breaks, so use section and identity boundaries.
            var rosterEnd = reportBefore.indexOf("LOCAL GAMEPLAY REVIEW (experimental; separate from website verdicts)");
            check(rosterEnd > reportBefore.indexOf("RECORDED ROSTER"), "The local roster section is missing.");
            var rosterText = reportBefore.slice(reportBefore.indexOf("RECORDED ROSTER"), rosterEnd).trim();
            localRoster = Array.from(rosterText.matchAll(/\b(\d{17}) \| (.*?)(?= \d{17} \| |$)/g), match =>
                ({ id: match[1], name: match[2].trim().replace(/\|/g, "/") }));
            var coverage = Array.from(reportBefore.matchAll(/decoded firearm shots (\d+); assessed (\d+); selected (\d+)/g));
            check(coverage.length > 0, "No local gameplay coverage was displayed.");
            var assessed = coverage.reduce((sum, match) => sum + Number(match[2]), 0);
            check(assessed > 0, "The imported demo has no eligible assessed shots.");
            previewBefore = value("GeminiPreviewBox");
            var request = JSON.parse(previewBefore);
            var summary = JSON.parse(request.contents[0].parts[0].text);
            keys(request, ["systemInstruction", "contents", "generationConfig"]);
            check(request.generationConfig.responseMimeType === "application/json",
                "Structured JSON output was not requested.");
            keys(summary, ["schema", "partialRecording", "selectionRule", "limitations",
                "totalSelectedEvents", "eventsIncluded", "eventSelection", "telemetryScope",
                "shotWindowScope", "engagementScope", "playerCoverage", "engagementCoverage", "engagements",
                "damageCoverage", "shotWindows", "roundTelemetry", "combatTimeline", "players", "events"]);
            check(summary.schema === "cs2-gameplay-telemetry-review-v4", "Expected engagement-level gameplay telemetry.");
            var audit = summary.playerCoverage;
            keys(audit, ["available", "columns", "rows"]);
            check(audit.available && audit.columns.join(",") ===
                "alias,observedRounds,observedEligibleTicks,eligibleShots,engagementGroups,retainedExamples,includedExamples,geometryAssessedShots,noOpponentShots,incompleteGeometryShots,includedCoverageRows" &&
                audit.rows.length === summary.players.length &&
                audit.rows.every((row, position) => row.length === 11 && row[0] === summary.players[position].alias &&
                    row.slice(1).every(value => Number.isInteger(value) && value >= 0)),
                "Complete compact player coverage is missing or malformed.");
            var localAudit = Array.from(reportBefore.matchAll(
                /observed eligible ticks (\d+); eligible shots (\d+); engagement groups (\d+); retained examples (\d+); geometry-assessed shots (\d+); no opposing humans observed (\d+); incomplete geometry shots (\d+);/g));
            check(localAudit.length > 0 && [2, 3, 4, 5, 7, 8, 9].every((column, position) =>
                audit.rows.reduce((sum, row) => sum + row[column], 0) ===
                    localAudit.reduce((sum, match) => sum + Number(match[position + 1]), 0)),
                "Submitted full player audits differ from the displayed local coverage.");
            var engagementCoverage = summary.engagementCoverage;
            keys(engagementCoverage, ["totalRows", "includedRows", "columns", "rows"]);
            check(engagementCoverage.columns.join(",") ===
                "alias,round,observedEligibleTicks,eligibleShots,engagements,retainedEngagements,geometryAssessedShots,noOpponentShots,incompleteGeometryShots,minimumEyeAngleDegrees,minimumEyeAngleTick" &&
                engagementCoverage.rows.length === engagementCoverage.includedRows &&
                engagementCoverage.totalRows === localAudit.length &&
                engagementCoverage.rows.every(row => row.length === 11 &&
                    (row[6] > 0 ? Number.isFinite(row[9]) && Number.isInteger(row[10]) : row[9] === null && row[10] === null)),
                "Selected round coverage has an invalid shape or missing-data representation.");
            var engagements = summary.engagements;
            keys(engagements, ["totalGroups", "retainedExamples", "includedExamples", "requestedTicksEachSide",
                "maximumShotSpanTicks", "delivery", "examples"]);
            check(engagements.requestedTicksEachSide === 32 && engagements.maximumShotSpanTicks === 256 &&
                engagements.includedExamples === engagements.examples.length && engagements.includedExamples > 0 &&
                engagements.includedExamples <= 16 &&
                engagements.retainedExamples === audit.rows.reduce((sum, row) => sum + row[5], 0) &&
                engagements.totalGroups === audit.rows.reduce((sum, row) => sum + row[4], 0),
                "Engagement retention/inclusion counts differ from the local audit.");
            engagements.examples.forEach(example => {
                keys(example, ["alias", "round", "firstShotTick", "lastShotTick", "weapon", "shots",
                    "beforeTicks", "afterTicks", "retainedPoints", "netTurnDegrees", "pathTurnDegrees", "movementPathUnits",
                    "pointColumns", "points", "retainedShotSnapshots", "shotColumns", "shotSnapshots",
                    "nearbyEventCount", "nearbyEventColumns", "nearbyEvents"]);
                var span = example.lastShotTick - example.firstShotTick;
                check(span >= 0 && span <= 256 && example.beforeTicks >= 0 && example.beforeTicks <= 32 &&
                    example.afterTicks >= 0 && example.afterTicks <= 32 &&
                    example.retainedPoints === example.beforeTicks + span + example.afterTicks + 1 &&
                    example.pointColumns.join(",") === "offsetTicks,pitchDegrees,yawRelativeDegrees,relativeX,relativeY,relativeZ" &&
                    example.points.length === Math.min(17, example.retainedPoints) &&
                    example.points.every((point, position) => point.length === 6 && point.every(Number.isFinite) &&
                        (position === 0 || point[0] > example.points[position - 1][0])) &&
                    example.points[0][0] === -example.beforeTicks &&
                    example.points[example.points.length - 1][0] === span + example.afterTicks,
                    "Bounded relative engagement trajectory has an invalid shape or coverage.");
                check(example.shotColumns.join(",") ===
                    "demoTick,opponentSnapshotAvailable,observedOpponents,assessedOpponents,closestAngularOpponent,eyeAngleDegrees,eyeDistanceUnits,relativeEyeX,relativeEyeY,relativeEyeZ,movementUnitsSincePreviousTick" &&
                    example.retainedShotSnapshots === example.shots &&
                    example.shotSnapshots.length === Math.min(8, example.retainedShotSnapshots) &&
                    example.shotSnapshots.every(shot => shot.length === 11 && typeof shot[1] === "boolean" &&
                        shot[2] >= shot[3] && shot[3] >= 0 &&
                        (shot[3] === 0 ? shot.slice(4, 10).every(value => value === null) :
                            summary.players.some(player => player.alias === shot[4]) &&
                            shot.slice(5, 10).every(Number.isFinite) && shot[5] >= 0 && shot[5] <= 180 && shot[6] > 0) &&
                        (shot[10] === null || Number.isFinite(shot[10]) && shot[10] >= 0)) &&
                    example.shotSnapshots[0][0] === example.firstShotTick &&
                    example.shotSnapshots[example.shotSnapshots.length - 1][0] === example.lastShotTick,
                    "Opponent geometry must distinguish missing measurements from zero angles.");
                check(example.nearbyEventColumns.join(",") === "eventId,kind,victim,demoTick,healthDamage,headshot" &&
                    example.nearbyEvents.length === Math.min(8, example.nearbyEventCount) &&
                    example.nearbyEvents.every(event => event.length === 6 && ["damage", "kill"].includes(event[1]) &&
                        event[3] >= example.firstShotTick && event[3] <= example.lastShotTick + 8),
                    "Engagement event context is invalid.");
            });
            var shotWindows = summary.shotWindows;
            keys(shotWindows, ["totalWindows", "retainedWindows", "includedWindows", "requestedTicksEachSide", "windows"]);
            check(shotWindows.includedWindows > 0 && shotWindows.includedWindows === shotWindows.windows.length &&
                shotWindows.requestedTicksEachSide === 8, "Missing multi-tick windows.");
            shotWindows.windows.forEach(window => {
                keys(window, ["alias", "round", "shotTick", "weapon", "beforeTicks", "afterTicks",
                    "beforeNetTurnDegrees", "beforePathTurnDegrees", "afterNetTurnDegrees", "afterPathTurnDegrees",
                    "sampleColumns", "samples", "nearbyEventCount", "nearbyEventsIncluded", "nearbyEventColumns", "nearbyEvents"]);
                check(window.sampleColumns.join(",") === "offsetTicks,pitchDegrees,yawRelativeToShotDegrees" &&
                    window.beforeTicks >= 0 && window.beforeTicks <= 8 && window.afterTicks >= 0 && window.afterTicks <= 8 &&
                    window.samples.length === window.beforeTicks + window.afterTicks + 1 &&
                    window.samples.every((point, index) => point.length === 3 && point[0] === index - window.beforeTicks &&
                        Number.isFinite(point[1]) && Number.isFinite(point[2])),
                    "Shot-window sequence coverage or shape is invalid.");
                check((window.beforeTicks > 0 || window.beforeNetTurnDegrees === null) &&
                    (window.afterTicks > 0 || window.afterNetTurnDegrees === null), "Missing window coverage must not imply zero turn.");
                check(window.nearbyEventColumns.join(",") === "eventId,kind,victim,demoTick,healthDamage,headshot" &&
                    window.nearbyEvents.length === window.nearbyEventsIncluded && window.nearbyEventsIncluded <= 8 &&
                    window.nearbyEventCount >= window.nearbyEventsIncluded &&
                    window.nearbyEvents.every(event => event.length === 6 && ["damage", "kill"].includes(event[1]) &&
                        event[3] >= window.shotTick && event[3] <= window.shotTick + 8),
                    "Temporal damage/death context is invalid.");
            });
            keys(summary.damageCoverage, ["totalEvents", "retainedEvents", "delivery"]);
            var rounds = summary.roundTelemetry;
            keys(rounds, ["totalRows", "includedRows", "columns", "rows"]);
            check(rounds.totalRows > 0 && rounds.includedRows === rounds.rows.length && rounds.rows.length > 0,
                "Round telemetry was not supplied.");
            check(rounds.columns.join(",") ===
                "alias,round,eligibleShots,assessedShots,meanTurnDegrees,maximumTurnDegrees,maximumTurnTick,firearmKills,headshotKills,firearmDeaths",
                "Unexpected round telemetry columns.");
            check(rounds.rows.every(row => row.length === rounds.columns.length),
                "A round telemetry row has the wrong shape.");
            var combat = summary.combatTimeline;
            keys(combat, ["totalEvents", "retainedEvents", "includedEvents", "columns", "rows"]);
            check(combat.columns.join(",") === "attacker,victim,round,demoTick,weapon,headshot" &&
                combat.rows.length === combat.includedEvents &&
                combat.rows.every(row => row.length === combat.columns.length),
                "Combat context has the wrong shape.");
            check(summary.players.length === coverage.length, "Preview player coverage differs from the displayed report.");
            check(localRoster.length === summary.players.length, "Local identity mapping does not cover every supplied player.");
            var schema = request.generationConfig.responseJsonSchema;
            check(schema && schema.properties.players.minItems === summary.players.length &&
                schema.properties.players.maxItems === summary.players.length,
                "Structured output must require a result for every alias.");
            check(schema.properties.commentary.type === "object" &&
                ["coverage", "alternativeExplanations", "missingEvidence"].every(field =>
                    schema.properties.commentary.required.includes(field)) &&
                schema.properties.players.items.required.includes("observation"),
                "Detailed review sections and individual player observations must be required.");
            check(summary.players.reduce((sum, player) => sum + player.assessedShots, 0) === assessed,
                "Preview assessed-shot count differs from the displayed report.");
            summary.players.forEach((player, position) => {
                keys(player, ["alias", "decodedFirearmShots", "assessedShots", "selectedShots", "roundsWithSelections"]);
                check(player.alias === "Player" + String(position + 1).padStart(2, "0"),
                    "Preview aliases are not regenerated.");
            });
            summary.events.forEach(event => keys(event,
                ["alias", "round", "demoTick", "weapon", "recordedTurnDegrees", "movementUnits"]));
            var ids = reportBefore.match(/\b\d{17}\b/g) || [];
            check(ids.length > 0 && ids.every(id => !previewBefore.includes(id)),
                "Preview identity check failed; do not send.");
            check(!previewBefore.includes(demoPath.split(/[\\/]/).pop()), "Preview includes the local demo filename.");
            check(node("GeminiDestinationText").includes(
                "https://generativelanguage.googleapis.com/v1beta/models/" + value("GeminiModelBox") + ":generateContent"),
                "Unexpected Gemini destination.");
            check(!read().includes("GeminiApproveBox"), "Obsolete approval control remains.");
            phase = "previewed";
            return "PASS: real import, " + coverage.length + " players, " + assessed +
                " assessed shots, " + rounds.includedRows + "/" + rounds.totalRows + " round rows, " +
                combat.includedEvents + "/" + combat.totalEvents +
                " combat events, " + shotWindows.includedWindows + "/" + shotWindows.totalWindows +
                " shot windows, " + engagements.includedExamples + "/" + engagements.totalGroups +
                " engagement examples/local groups, full player audits, allowlisted preview and Google destination. Obtain operator authorization before a live test submission.";
        },
        submitOnceAfterUserConfirmation: function () {
            check(phase === "previewed", "A fresh verified preview is required; submission is never retried.");
            check(value("GeminiPreviewBox") === previewBefore, "Preview changed; obtain fresh approval.");
            phase = "submission-attempted";
            check(!node("SendGeminiButton").includes("(disabled)"), "A saved key and prepared request must enable Send.");
            click("SendGeminiButton");
            return "One UI Send attempted. Observe completion; never retry a failed or uncertain send automatically.";
        },
        verifyResponseWhenReady: function () {
            check(phase === "submission-attempted", "Submit once only after the user's specific confirmation.");
            if (!node("CancelButton").includes("(disabled)"))
                return "WAIT: Gemini request is still running; observe again later.";
            tab("Final result");
            var response = value("FinalResultBox").split("DEMO | UNVERIFIED GEMINI COMMENTARY ---").pop().trim();
            var disclaimer = "No website flags, coverage or rankings were changed.";
            var disclaimerEnd = response.indexOf(disclaimer);
            check(response.startsWith("UNVERIFIED AI COMMENTARY |") &&
                disclaimerEnd >= 0 && response.slice(disclaimerEnd + disclaimer.length).trim().length > 0,
                "No usable labeled Gemini commentary; inspect app status. No automatic retry.");
            var marker = "FINAL PLAYER REVIEW TABLE";
            check(response.includes(marker) && response.includes("does not clear a player"),
                "The final unverified player table or its limitations are missing.");
            var previousSection = -1;
            ["Coverage", "Player observations", "Review moments with reasons", "Alternative explanations",
                "Missing evidence", marker].forEach(heading => {
                    var position = response.indexOf(heading, previousSection + 1);
                    check(position > previousSection, "A required detailed section is missing or out of order.");
                    previousSection = position;
                });
            check(response.includes("Measured coverage:") &&
                (response.match(/AI observation:/g) || []).length === localRoster.length &&
                (response.match(/Measured basis:/g) || []).length === localRoster.length,
                "Each player needs detailed observations and a local summary of submitted measurements.");
            var table = response.slice(response.lastIndexOf(marker));
            check(Array.from(table.matchAll(/\(Player\d+\) \| \d{17} \| /g)).length === localRoster.length,
                "The final table does not have exactly one row per imported player.");
            var starts = localRoster.map((player, position) => {
                var prefix = player.name + " (Player" + String(position + 1).padStart(2, "0") + ") | " + player.id + " | ";
                check(table.split(prefix).length === 2, "A final table identity is missing or duplicated.");
                return table.indexOf(prefix);
            });
            check(starts.every((start, position) => position === 0 || start > starts[position - 1]),
                "Final player table order differs from the roster.");
            var rows = starts.map((start, position) => table.slice(start,
                position + 1 < starts.length ? starts[position + 1] : table.length).trim());
            rows.forEach((row, position) => {
                var player = localRoster[position];
                var alias = "Player" + String(position + 1).padStart(2, "0");
                check(row.startsWith(player.name + " (" + alias + ") | " + player.id + " | "),
                    "A final table name/ID/alias does not match the imported roster.");
                var status = row.split(" | ")[2];
                check(["Review suggested", "No review concern identified", "Insufficient data"].includes(status),
                    "A player has an unsupported review label.");
                check(row.split(" | ")[3].trim().length > 0, "A final player row has no reason.");
                if (status === "Review suggested")
                    check(/ \| round \d+, tick \d+/.test(row), "Suggested review lacks a supplied moment.");
            });
            check(response.trimEnd().endsWith(rows[rows.length - 1]),
                "The player table must be at the very end of the response.");
            check(!read().includes("GeminiApproveBox"), "Obsolete approval control remains.");
            check(!value("FinalResultBox").includes("VERDICT:"), "Website verdict leaked into the demo result.");
            tab("Evidence");
            check(value("GeminiPreviewBox") === previewBefore, "The submitted preview changed.");
            check(value("ReportBox") === reportBefore, "Local demo measurements changed.");
            check(selectWebsite() === (finalBefore !== undefined), "Website report availability changed.");
            if (finalBefore !== undefined) {
                check(value("ReportBox") === evidenceBefore, "Website evidence changed.");
                check(!read().includes(" ID: GeminiPreviewBox"), "Demo preview leaked into website evidence.");
                tab("Final result");
                check(value("FinalResultBox") === finalBefore, "Website result changed.");
            }
            phase = "passed";
            return "PASS: desktop import -> optional summary preview -> real Gemini send -> commentary and complete locally mapped player table; no approval checkbox, local and website reports unchanged. Response text and identities were not logged.";
        }
    };
})();
