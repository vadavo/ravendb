/// <reference path="../../typings/tsd.d.ts"/>

class jsonUtil {
    static syntaxHighlight(json: any) {
        if (typeof json != 'string') {
            json = JSON.stringify(json, undefined, 2);
        }
        json = json.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        return json.replace(/("(\\u[a-zA-Z0-9]{4}|\\[^u]|[^\\"])*"(\s*:)?|\b(true|false|null)\b|-?\d+(?:\.\d*)?(?:[eE][+-]?\d+)?)/g, function (match: string) {
            let cls = 'number';
            if (/^"/.test(match)) {
                if (/:$/.test(match)) {
                    cls = 'key';
                } else {
                    cls = 'string';
                }
            } else if (/true|false/.test(match)) {
                cls = 'boolean';
            } else if (/null/.test(match)) {
                cls = 'null';
            }
            return '<span class="' + cls + '">' + match + '</span>';
        });
    }

    static newLineNormalizingHashFunction = (object: any) => {
        return ko.toJSON(object).replace(/\\r\\n/g, '\\n');
    };
    
    static newLineNormalizingHashFunctionWithIgnoredFields = (ignoredFields: string[]) => {
        return (object: any) => {
            return ko.toJSON(object, (k:string, v:string) => ignoredFields.indexOf(k) == -1 ? v : null).replace(/\\r\\n/g, '\\n');
        }
    }
} 

export = jsonUtil
