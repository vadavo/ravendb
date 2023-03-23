/// <reference path="../../../typings/tsd.d.ts"/>
import genUtils = require("common/generalUtils");

class ipEntry {
    
   ip = ko.observable<string>();
   validationGroup: KnockoutValidationGroup;
   
   isLocalNetwork: KnockoutComputed<boolean>;

   static runningOnDocker = false;
   
   constructor(allowHostname: boolean) {
       
       const extenders = {
           required: true
       } as any;
       
       if (allowHostname) {
           extenders.validAddressWithoutPort = true;
       } else {
           extenders.validIpWithoutPort = true;
       }
       
       extenders.validation = [
           {
               validator: (ip: string) => (ipEntry.runningOnDocker && !genUtils.isLocalhostIpAddress(ip)) || !ipEntry.runningOnDocker,
               message: "A localhost IP Address is not allowed when running on Docker"
           },
           {
               validator: (ip: string) => !_.startsWith(ip, "http://") && !_.startsWith(ip, "https://"),
               message: "Expected valid IP Address/Hostname, not URL"
           }];
       
       
       this.ip.extend(extenders);
       
       this.validationGroup = ko.validatedObservable({
           ip: this.ip
       });
       
       this.isLocalNetwork = ko.pureComputed(() => {
           const ip = this.ip();
           
           if (!ip || !this.validationGroup.isValid()) {
               return false;
           }
           
           return ip === "localhost" || ip === "::1" || ip.startsWith("127.");
       });
   }
   
   static forIp(ip: string, allowHostname: boolean) {
       const entry = new ipEntry(allowHostname);
       entry.ip(ip);
       return entry;
   }
}

export = ipEntry;
