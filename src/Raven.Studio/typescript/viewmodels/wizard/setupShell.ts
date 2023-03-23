/// <reference path="../../../typings/tsd.d.ts" />
import router = require("plugins/router");
import sys = require("durandal/system");
import setupRoutes = require("common/setup/routes");
import getClientBuildVersionCommand = require("commands/database/studio/getClientBuildVersionCommand");
import getServerBuildVersionCommand = require("commands/resources/getServerBuildVersionCommand");
import messagePublisher = require("common/messagePublisher");
import extensions = require("common/extensions");
import viewModelBase = require("viewmodels/viewModelBase");
import autoCompleteBindingHandler = require("common/bindingHelpers/autoCompleteBindingHandler");
import requestExecution = require("common/notifications/requestExecution");
import protractedCommandsDetector = require("common/notifications/protractedCommandsDetector");
import buildInfo = require("models/resources/buildInfo");
import chooseTheme = require("viewmodels/shell/chooseTheme");
import app = require("durandal/app");
import serverSetup from "models/wizard/serverSetup";

class setupShell extends viewModelBase {

    view = require("views/wizard/setupShell.html");

    private router = router;
    studioLoadingFakeRequest: requestExecution;
    clientBuildVersion = ko.observable<clientBuildVersionDto>();
    static deploymentEnvironment = serverSetup.deploymentEnvironment;
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
        router.map(setupRoutes.get()).buildNavigationModel();

        router.mapUnknownRoutes((instruction: DurandalRouteInstruction) => {
            const queryString = instruction.queryString ? ("?" + instruction.queryString) : "";

            messagePublisher.reportError("Unknown route", "The route " + instruction.fragment + queryString + " doesn't exist, redirecting...");

            window.location.href = "#welcome";
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

    static chooseTheme() {
        const dialog = new chooseTheme();
        app.showBootstrapDialog(dialog);
    }
}

export = setupShell;
