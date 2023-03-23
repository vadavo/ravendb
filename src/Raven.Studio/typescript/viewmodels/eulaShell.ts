/// <reference path="../../typings/tsd.d.ts" />

import router = require("plugins/router");
import sys = require("durandal/system");
import eulaRoutes = require("common/eula/routes");
import getClientBuildVersionCommand = require("commands/database/studio/getClientBuildVersionCommand");
import getServerBuildVersionCommand = require("commands/resources/getServerBuildVersionCommand");
import messagePublisher = require("common/messagePublisher");
import extensions = require("common/extensions");
import viewModelBase = require("viewmodels/viewModelBase");
import autoCompleteBindingHandler = require("common/bindingHelpers/autoCompleteBindingHandler");
import requestExecution = require("common/notifications/requestExecution");
import protractedCommandsDetector = require("common/notifications/protractedCommandsDetector");
import buildInfo = require("models/resources/buildInfo");

class eulaShell extends viewModelBase {

    view = require("views/eulaShell.html");

    private router = router;
    studioLoadingFakeRequest: requestExecution;
    clientBuildVersion = ko.observable<clientBuildVersionDto>();
    static buildInfo = buildInfo;

    showSplash = viewModelBase.showSplash;

    constructor() {
        super();

        autoCompleteBindingHandler.install();

        this.studioLoadingFakeRequest = protractedCommandsDetector.instance.requestStarted(0);
        
        extensions.install();
    }

    // Override canActivate: we can always load this page, regardless of any system db prompt.
    canActivate(): any {
        return true;
    }

    activate(args: any) {
        super.activate(args, { shell: true });

        this.setupRouting();
        
        return this.router.activate()
            .then(() => {
                this.fetchClientBuildVersion();
                this.fetchServerBuildVersion();
            })
    }

    private fetchServerBuildVersion() {
        new getServerBuildVersionCommand()
            .execute()
            .done((serverBuildResult: serverBuildVersionDto) => {
                buildInfo.onServerBuildVersion(serverBuildResult);

              
            });
    }

    private fetchClientBuildVersion() {
        new getClientBuildVersionCommand()
            .execute()
            .done((result: clientBuildVersionDto) => {
                this.clientBuildVersion(result);
                viewModelBase.clientVersion(result.Version);
            });
    }

    private setupRouting() {
        router.map(eulaRoutes.get()).buildNavigationModel();

        router.mapUnknownRoutes((instruction: DurandalRouteInstruction) => {
            const queryString = instruction.queryString ? ("?" + instruction.queryString) : "";

            messagePublisher.reportError("Unknown route", "The route " + instruction.fragment + queryString + " doesn't exist, redirecting...");

            location.href = "#license";
        });
    }

    attached() {
        super.attached();

        sys.error = (e: any) => {
            console.error(e);
            messagePublisher.reportError("Failed to load routed module!", e);
        };
    }

    compositionComplete() {
        super.compositionComplete();
        $("body")
            .removeClass('loading-active')
            .addClass("setup-shell");
        $(".loading-overlay").remove();

        this.studioLoadingFakeRequest.markCompleted();
        this.studioLoadingFakeRequest = null;
    }
}

export = eulaShell;
