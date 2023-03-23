/// <reference path="../../../typings/tsd.d.ts"/>

class routes {

    static get(): Array<DurandalRouteConfiguration> {
        const routes: DurandalRouteConfiguration[] = [
            {
                route: ["", "welcome"],
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/welcome"),
                nav: false
            },{
                route: "security",
                title: "RavenDB Setup Wizard",
                moduleId: require("viewmodels/wizard/security"),
                nav: false
            },{
                route: "unsecured",
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/unsecured"),
                nav: false
            },{
                route: "finish",
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/finish"),
                nav: false
            }, {
                route: "continue",
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/continueConfiguration"),
                nav: false
            },{
                route: "license",
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/license"),
                nav: false
            },{
                route: "domain",
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/domain"),
                nav: false
            },{
                route: "nodes",
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/nodes"),
                nav: false
            },{
                route: "certificate",
                title: "RavenDB Setup wizard",
                moduleId: require("viewmodels/wizard/certificate"),
                nav: false
            }
        ];

        return routes;
    }

}

export = routes;
