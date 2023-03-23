import widget = require("viewmodels/resources/widgets/widget");
import license = require("models/auth/licenseModel");
import appUrl = require("common/appUrl");
import generalUtils = require("common/generalUtils");
import getCertificatesCommand = require("commands/auth/getCertificatesCommand");
import accessManager = require("common/shell/accessManager");
import clusterDashboard = require("viewmodels/resources/clusterDashboard");
import moment = require("moment");

interface serverCertificateInfo {
    dateFormatted: string;
    durationFormatted: string;
    expirationClass: string;
}

class licenseWidget extends widget {

    view = require("views/resources/widgets/licenseWidget.html");

    refreshIntervalId = -1;
    isSecureServer = accessManager.default.secureServer();
    
    spinners = {
        serverCertificate: ko.observable<boolean>()
    }
    
    licenseTypeText = license.licenseTypeText;
    formattedExpiration = license.formattedExpiration;
    
    serverCertificateInfo = ko.observable<serverCertificateInfo>();

    aboutPageUrl = appUrl.forAbout();
    
    constructor(controller: clusterDashboard) {
        super(controller);
        
        this.spinners.serverCertificate(this.canLoadCertificateInfo());
    }

    private canLoadCertificateInfo() {
        return this.isSecureServer && accessManager.default.isOperatorOrAbove();
    }

    compositionComplete() {
        super.compositionComplete();
        
        if (this.canLoadCertificateInfo()) {
            this.loadServerCertificate();
            this.refreshIntervalId = setInterval(() => this.loadServerCertificate(), 3600 * 1000);
        }
    }
    
    private loadServerCertificate() {
        new getCertificatesCommand(false)
            .execute()
            .done(certificatesInfo => {
                const serverCertificateThumbprint = certificatesInfo.LoadedServerCert;
                const serverCertificate = certificatesInfo.Certificates.find(x => x.Thumbprint === serverCertificateThumbprint);

                const date = moment.utc(serverCertificate.NotAfter);
                const dateFormatted = date.format("YYYY-MM-DD");

                const nowPlusMonth = moment.utc().add(1, 'months');
                
                let expirationClass = "";

                if (date.isBefore()) {
                    expirationClass = "text-danger";
                } else if (date.isAfter(nowPlusMonth)) {
                    // valid for at least 1 month - use defaults
                } else {
                    expirationClass = "text-warning";
                }
                
                const durationFormatted = generalUtils.formatDurationByDate(date, true);

                this.serverCertificateInfo({
                    dateFormatted,
                    expirationClass,
                    durationFormatted
                });
            })
            .always(() => this.spinners.serverCertificate(false));
    }

    isCloud = ko.pureComputed(() => {
        const licenseStatus = license.licenseStatus();
        return licenseStatus && licenseStatus.IsCloud;
    });

    expiresText = ko.pureComputed(() => {
        const licenseStatus = license.licenseStatus();
        if (!licenseStatus || !licenseStatus.Expiration) {
            return null;
        }

        return licenseStatus.IsIsv ? "Updates Expiration" : "License Expiration";
    });

    supportLabel = license.supportLabel;

    automaticRenewText = ko.pureComputed(() => {
        return this.isCloud() ? "Cloud licenses are automatically renewed" : "";
    });

    getType(): widgetType {
        return "License";
    }
    
    dispose() {
        super.dispose();
        
        if (this.refreshIntervalId !== -1) {
            clearInterval(this.refreshIntervalId);
        }
    }

}

export = licenseWidget;
