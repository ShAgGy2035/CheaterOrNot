// Load into the Computer Use exec_js runtime after reading its documentation.
var videoDesktopE2E = (() => {
    var app, demoPath, phase = "new", finalBefore, evidenceBefore, priorDemoFinal, priorDemoEvidence,
        rosterIds = [], videoAliases = [], captured = false, automatic = false;
    function check(condition, message) { if (!condition) throw Error(message); }
    function read() {
        return app.getAXState({ emit: false, disableDiffing: true, maxDepth: 10, maxChildren: 1000 });
    }
    function index(line) {
        var match = line.match(/^\s*(\d+) AX/);
        check(match !== null, "Missing accessible index.");
        return Number(match[1]);
    }
    function node(id, role) {
        var matches = read().split("\n").filter(line => (!role || line.includes(role)) &&
            (line.includes(" ID: " + id + " ") || line.endsWith(" ID: " + id)));
        check(matches.length === 1, "Expected one visible control: " + id);
        return matches[0];
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
        var lines = read().split("\n").filter(line => /AXTab \((selected|selectable)\)/.test(line) &&
            line.includes(") " + label + " screen@"));
        check(lines.length === 1, "Missing tab: " + label);
        app.click(index(lines[0]));
    }
    function importedRoster() {
        var report = value("ReportBox");
        var rosterStart = report.indexOf("RECORDED ROSTER");
        var rosterEnd = report.indexOf("LOCAL GAMEPLAY REVIEW");
        check(rosterStart >= 0 && rosterEnd > rosterStart &&
            /\b\d{17} \|/.test(report.slice(rosterStart, rosterEnd)), "No imported human roster.");
        rosterIds = report.slice(rosterStart, rosterEnd).match(/\b\d{17}(?= \|)/g) || [];
    }
    function selectWebsite() {
        var website = read().split("\n").find(line => line.includes(" ID: WebsiteReportButton") && !line.includes("(disabled)"));
        if (!website) return false;
        app.click(index(website));
        return true;
    }
    function checkSeparateReports() {
        tab("Final result");
        var demoFinal = value("FinalResultBox");
        check(demoFinal.startsWith("DEMO SUMMARY |") && !demoFinal.includes("VERDICT:"), "Demo result includes an unrelated website report.");
        tab("Evidence");
        var demoEvidence = value("ReportBox");
        check(demoEvidence.includes("RECORDED ROSTER") && !demoEvidence.includes("Review indicators, not a cheating verdict"), "Demo evidence includes website data.");
        check(selectWebsite() === (finalBefore !== undefined), "Website report availability changed.");
        if (finalBefore !== undefined) {
            check(value("ReportBox") === evidenceBefore, "Website evidence changed.");
            tab("Final result");
            check(value("FinalResultBox") === finalBefore, "Website result changed.");
            click("DemoReportButton");
            check(value("FinalResultBox") === demoFinal, "Demo result changed after switching.");
            tab("Evidence");
            check(value("ReportBox") === demoEvidence, "Demo evidence changed after switching.");
        }
    }
    return {
        begin: function (target, authorizedDemoPath, analyzeWithGemini = false) {
            check(phase === "new", "Use a fresh driver for each run.");
            check(typeof authorizedDemoPath === "string" && /\.dem$/i.test(authorizedDemoPath), "Supply an authorized local demo.");
            demoPath = authorizedDemoPath;
            automatic = analyzeWithGemini;
            app = cua.getApp(target, { maxDepth: 1 });
            check(node("CancelButton").includes("(disabled)"), "Do not interrupt an active operation.");
            if (selectWebsite()) {
                tab("Final result");
                finalBefore = value("FinalResultBox");
                tab("Evidence");
                evidenceBefore = value("ReportBox");
            }
            click(automatic ? "ImportGeminiDemoButton" : "ImportDemoButton");
            phase = "picker";
            return automatic
                ? "Analysis picker opened. Selecting the demo starts fresh local capture/preparation, then pauses for explicit clip/request upload confirmation."
                : "Native demo picker opened; no online request.";
        },
        selectDemo: function () {
            check(phase === "picker", "Open the picker first.");
            var state = read();
            check(state.includes(automatic ? "Analyze CS2 demo with Gemini (uploads video)" :
                "AXWindow Import a local CS2 demo (no upload)"), "Wrong picker.");
            var fields = state.split("\n").filter(line => line.includes("AXTextField") &&
                line.includes("File name:") && line.includes(" ID: 1148 "));
            check(fields.length === 1, "Missing native file-name field.");
            app.setValue(index(fields[0]), demoPath);
            click("1", "AXButton");
            phase = automatic ? "analyzing" : "importing";
            return automatic ? "Fresh-capture analysis started; approve or decline only after the app shows the exact upload counts." : "Real local demo import started.";
        },
        confirmAutomaticUploadAfterAuthorization: function () {
            check(phase === "analyzing", "Start automatic analysis and wait for the upload confirmation.");
            check(read().includes(" ID: ConfirmGeminiUploadButton"), "The Gemini upload confirmation is not open.");
            click("ConfirmGeminiUploadButton");
            return "Explicitly confirmed the displayed Gemini clip/request counts; uploads may now begin.";
        },
        declineAutomaticUpload: function () {
            check(phase === "analyzing", "Start automatic analysis and wait for the upload confirmation.");
            check(read().includes(" ID: DeclineGeminiUploadButton"), "The Gemini upload confirmation is not open.");
            click("DeclineGeminiUploadButton");
            return "Declined all Gemini uploads. Fresh capture files should remain local.";
        },
        verifyLocalReportSeparationWhenReady: function () {
            check(phase === "importing", "Start a local demo import first.");
            if (!node("CancelButton").includes("(disabled)")) return "WAIT: importing.";
            check(finalBefore !== undefined, "Start with a completed website report to test both report views.");
            checkSeparateReports();
            phase = "separation-passed";
            return "PASS: local demo and prior website reports remain separate in both tabs; no Gemini upload.";
        },
        verifyPendingLocalImport: function () {
            check(phase === "importing", "Start a local demo import with a report already loaded.");
            var state = read();
            if (!state.includes("Importing demo on a background worker"))
                return "NOT OBSERVED: use a demo large enough to inspect while it is importing.";
            check(!state.includes(" ID: ReportSelector"), "Report switch is visible during import.");
            check(node("SaveButton").includes("(disabled)"), "The old report can still be saved.");
            tab("Final result");
            check(value("FinalResultBox") === "", "An old final result is still visible.");
            tab("Evidence");
            check(value("ReportBox") === "", "Old evidence is still visible.");
            return "PASS: both tabs are empty while importing.";
        },
        beginFailedReplacement: function (invalidDemoPath) {
            check(phase === "separation-passed", "First verify a completed local demo and website report.");
            check(typeof invalidDemoPath === "string" && /\.dem$/i.test(invalidDemoPath), "Supply a local invalid .dem fixture.");
            tab("Final result");
            priorDemoFinal = value("FinalResultBox");
            tab("Evidence");
            priorDemoEvidence = value("ReportBox");
            demoPath = invalidDemoPath;
            click("ImportDemoButton");
            phase = "picker";
            return "Import picker opened for the invalid demo fixture; call selectDemo.";
        },
        verifyFailedReplacementWhenReady: function () {
            check(phase === "importing" && priorDemoFinal !== undefined, "Select the invalid demo fixture first.");
            if (!node("CancelButton").includes("(disabled)")) return "WAIT: importing invalid demo.";
            check(node("StatusText").includes("Could not import demo"), "The invalid demo did not report an import failure.");
            tab("Final result");
            check(value("FinalResultBox") === priorDemoFinal, "Failed import did not restore the previous demo result.");
            tab("Evidence");
            check(value("ReportBox") === priorDemoEvidence, "Failed import did not restore the previous demo evidence.");
            checkSeparateReports();
            phase = "failed-replacement-passed";
            return "PASS: failed import restored the previous demo, and the website report stayed separate.";
        },
        finishAnalysisWhenReady: function () {
            check(phase === "analyzing", "Start begin(target, demoPath, true), then select the specifically authorized demo.");
            if (read().includes(" ID: ConfirmGeminiUploadButton"))
                return "WAIT: review the clip/request counts, then call confirmAutomaticUploadAfterAuthorization() or declineAutomaticUpload().";
            if (!node("CancelButton").includes("(disabled)")) return "WAIT: automatic demo analysis.";
            if (node("StatusText").includes("No video or telemetry was sent to Gemini")) {
                phase = "analysis-declined";
                return "PASS: fresh clips were captured locally and Gemini upload was explicitly declined; no network upload occurred.";
            }
            check(node("StatusText").includes("Demo analysis complete."), "Analysis failed; inspect the visible status. Do not retry automatically.");
            tab("Evidence");
            importedRoster();
            var previewToggle = read().split("\n").find(line => line.includes("Exact request preview") &&
                /AX(Button|DisclosureTriangle)/.test(line));
            check(previewToggle, "Exact request preview toggle missing.");
            app.click(index(previewToggle));
            var preview = value("GeminiPreviewBox");
            check(preview.includes("VIDEO REVIEW REQUEST PREVIEW") &&
                preview.includes("[MP4 binary omitted from this text display") &&
                /Exact JSON request: \d+ UTF-8 bytes; SHA256 [A-F0-9]{64}/.test(preview), "Automatic video request preview is missing.");
            check(!node("CopyGeminiButton").includes("(disabled)") &&
                !read().includes(" ID: SendGeminiButton"), "Video preview must be copyable, not sendable as a summary.");
            tab("Final result");
            var commentary = value("FinalResultBox");
            ["UNVERIFIED AI VIDEO COMMENTARY", "Coverage", "Player observations", "Review moments with reasons",
                "Alternative explanations", "Missing evidence", "FINAL PLAYER REVIEW TABLE", "Saved locally:"]
                .forEach(heading => check(commentary.includes(heading), "Missing analysis section: " + heading));
            rosterIds.forEach(id => check(commentary.includes(id), "Missing local player mapping."));
            check(!read().includes("GeminiApproveBox"), "Obsolete approval control remains.");
            checkSeparateReports();
            phase = "analysis-passed";
            return "PASS: file selection -> import -> fresh verified capture -> confirmed batched video+telemetry requests -> saved combined mapped result; website reports unchanged.";
        },
        prepareWhenReady: function () {
            check(phase === "importing", "Import a demo first.");
            if (!node("CancelButton").includes("(disabled)")) return "WAIT: importing.";
            tab("Evidence");
            importedRoster();
            click("VideoComponentsButton");
            var state = read();
            check(state.includes("No video is sent to Google"), "Missing local-only notice.");
            click("CaptureVideoButton");
            check(!node("CancelVideoButton").includes("(disabled)"), "Capture did not start.");
            check(!read().includes("UpdateCaptureToolsButton"), "Maintenance actions must be hidden by default.");
            phase = "capturing";
            captured = true;
            return "Real CS2 capture started. This is not a mocked encoder test.";
        },
        finishWhenReady: function () {
            check(phase === "capturing", "Start capture first.");
            var state = read();
            if (!node("CancelVideoButton").includes("(disabled)")) return "WAIT: rendering / encoding.";
            check(/Verified \d+ local MP4 clips; \d+ have an audio stream/.test(state), "Capture failed; inspect the visible error.");
            check(!node("OpenVideoFolderButton").includes("(disabled)"), "Missing output-folder action.");
            var close = state.split("\n").filter(line => /AXButton Close ID: Close/.test(line));
            check(close.length === 1, "Expected only the capture dialog close button.");
            app.click(index(close[0]));
            checkSeparateReports();
            click("VideoComponentsButton");
            check(!node("OpenVideoFolderButton").includes("(disabled)"), "Saved output must remain accessible after reopening.");
            var reopenedClose = read().split("\n").filter(line => /AXButton Close ID: Close/.test(line));
            check(reopenedClose.length === 1, "Expected only the reopened capture dialog close button.");
            app.click(index(reopenedClose[0]));
            phase = "passed";
            return "PASS: real import, CS2 rendering, MP4 file checks and unchanged website reports. Separately inspect every clip's requested POV, X-ray and timing; process/file checks alone cannot validate footage content.";
        },
        cancel: function () {
            check(phase === "capturing", "No capture is active.");
            click("CancelVideoButton");
            phase = "canceling";
            return "Safe stop requested; the current engine step must finish before confirmation.";
        },
        confirmCanceled: function () {
            check(phase === "canceling", "Request cancellation first.");
            var state = read();
            if (node("CaptureVideoButton").includes("(disabled)")) return "WAIT: canceling.";
            check(state.includes("Stopped safely. No complete result was accepted"), "The engine step did not finish safely after the stop request.");
            phase = "canceled";
            return "PASS: stopped at a safe engine-step boundary; no success-shaped result.";
        },
        prepareVideoReview: function () {
            check(phase === "passed", "Complete the real capture flow first.");
            click("VideoComponentsButton");
            click("PreviewVideoAiButton");
            phase = "video-preparing";
            return "Preparing real local MP4 copies and video request. Nothing sent.";
        },
        prepareExistingVideoReviewWhenReady: function () {
            check(phase === "importing", "Import the source demo first.");
            if (!node("CancelButton").includes("(disabled)")) return "WAIT: importing.";
            tab("Evidence");
            importedRoster();
            click("VideoComponentsButton");
            click("PreviewVideoAiButton");
            phase = "video-preparing";
            return "Preparing existing source-bound capture locally. This mode does not claim a new CS2 capture run.";
        },
        inspectVideoPreviewWhenReady: function () {
            check(phase === "video-preparing", "Prepare video review first.");
            if (!node("CancelVideoAiButton").includes("(disabled)")) return "WAIT: preparing local video.";
            var preview = value("VideoAiPreviewBox");
            check(preview.includes("VIDEO REVIEW REQUEST PREVIEW") &&
                preview.includes("NOT anonymized") && preview.includes("SHA256") &&
                preview.includes("generativelanguage.googleapis.com") && preview.includes("inlineData"),
                "Missing video request, integrity or privacy preview.");
            check(!read().includes("ApproveVideoAiBox"), "Obsolete video approval control remains.");
            var structure = JSON.parse(preview.slice(preview.indexOf("{", preview.indexOf("JSON structure below"))));
            var descriptors = structure.contents[0].parts.filter(part => typeof part.text === "string")
                .map(part => JSON.parse(part.text)).filter(descriptor => typeof descriptor.Alias === "string");
            descriptors.forEach(descriptor => {
                var timeline = descriptor.EventTimeline;
                check(timeline && timeline.Number === descriptor.Number && timeline.Alias === descriptor.Alias &&
                    timeline.Round === descriptor.Round && timeline.StartTick === descriptor.StartTick &&
                    timeline.EndTick === descriptor.EndTick, "Missing or mismatched local clip event context.");
                [timeline.RetainedShots, timeline.RetainedKills, timeline.RetainedDeaths].forEach(events => {
                    check(Array.isArray(events), "Missing retained event array.");
                    events.forEach(event => check(Number.isInteger(event.Tick) &&
                        event.Tick >= descriptor.StartTick && event.Tick <= descriptor.EndTick &&
                        event.NominalSeconds === (event.Tick - descriptor.StartTick) / 64,
                        "Invalid nominal event offset; this does not verify video synchronization."));
                });
            });
            videoAliases = Array.from(new Set(descriptors.map(descriptor => descriptor.Alias)));
            check(videoAliases.length > 0, "No actual video descriptors in the prepared request.");
            phase = "video-preview-ready";
            return "PASS: real source-bound advanced preview ready; no upload yet. Obtain operator authorization before sending.";
        },
        sendVideoAfterApproval: function () {
            check(phase === "video-preview-ready", "Inspect the exact local video copies and obtain specific send approval first.");
            check(!node("SendVideoAiButton").includes("(disabled)"), "Save a Gemini key before the E2E send.");
            click("SendVideoAiButton");
            phase = "video-confirming";
            return "The app is showing the exact request counts. Confirm them only with specific operator authorization.";
        },
        confirmVideoUploadAfterAuthorization: function () {
            check(phase === "video-confirming", "Open the advanced upload confirmation first.");
            check(read().includes(" ID: ConfirmGeminiUploadButton"), "The Gemini upload confirmation is not open.");
            click("ConfirmGeminiUploadButton");
            phase = "video-sending";
            return "Explicitly confirmed the prepared Gemini request batches; not a mocked HTTP test.";
        },
        finishVideoReviewWhenReady: function () {
            check(phase === "video-sending", "Send an approved video review first.");
            if (!node("CancelVideoAiButton").includes("(disabled)")) return "WAIT: Gemini video review.";
            var status = node("VideoAiStatus");
            check(status.includes("All batches passed validation."), "Video review did not complete: " + status);
            var commentary = value("VideoAiResponseBox");
            check(commentary.includes("UNVERIFIED AI VIDEO COMMENTARY"), "No usable video commentary; inspect visible error.");
            ["Coverage", "Player observations", "Review moments with reasons", "Alternative explanations",
                "Missing evidence", "FINAL PLAYER REVIEW TABLE", "Saved locally:"].forEach(heading =>
                    check(commentary.includes(heading), "Missing video review section: " + heading));
            rosterIds.forEach(id => check(commentary.includes(id), "Missing locally mapped player."));
            videoAliases.forEach(alias => {
                var start = commentary.indexOf("- " + alias + " | Measured basis:");
                var end = commentary.indexOf("- Player", start + 1);
                if (end < 0) end = commentary.indexOf("Review moments with reasons", start);
                check(start >= 0 && end > start &&
                    commentary.slice(start, end).includes("Video observation ("),
                    "Missing timestamped scene observation for " + alias);
            });
            check(!read().includes("ApproveVideoAiBox"), "Obsolete video approval control remains.");
            click("Close", "AXButton");
            click("Close", "AXButton");
            checkSeparateReports();
            phase = "video-passed";
            return "PASS: actual import, " + (captured ? "CS2 capture" : "existing source-bound capture") +
                ", verified local copies, approved inline Gemini video generation, timestamped scene observations, detailed player table, local saved result and unchanged website reports.";
        },
        status: function () { return { phase: phase }; }
    };
})();
