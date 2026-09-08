import {renderTriggerEvent} from './shared/unreal/generate-trigger-events.mjs';
import {readFileSync,writeFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const specification=JSON.parse(readFileSync(path.join(root,'metroidvania-studio/contracts/trigger-events.json'),'utf8'));
const names=['None',...Array.from({length:specification.eventCount},(_,i)=>'Trigger'+String(i+1).padStart(3,'0'))];
if(specification.formatVersion!==1||names.length!==201)throw new Error('Trigger event numbers are a stable runtime contract.');
const catalog=JSON.parse(readFileSync(path.join(root,'samples/catalog.json'),'utf8'));
for(const id of ['Area','Portal']) {
 const field=catalog.objects.find(o=>o.id===id)?.properties.find(p=>p.key==='event');
 if(!field||JSON.stringify(field.choices)!==JSON.stringify(names))throw new Error('Built-in event choices do not match the runtime contract: '+id);
}
const generated='// Generated from contracts/trigger-events.json by integrations/generate-trigger-events.mjs.\n';
const csharp=namespace=>generated+`using System;\nusing System.Globalization;\nnamespace ${namespace}\n{\n    public enum StudioTriggerEvent\n    {\n${names.map((n,i)=>`        ${n} = ${i}`).join(',\n')}\n    }\n    public static class StudioTriggerEvents\n    {\n        public static bool TryParse(string value, out StudioTriggerEvent result)\n        {\n            result = StudioTriggerEvent.None;\n            value = (value ?? "").Trim();\n            if (value == "" || value.Equals("None", StringComparison.OrdinalIgnoreCase)) return true;\n            string number = value;\n            if (value.StartsWith("Trigger", StringComparison.OrdinalIgnoreCase))\n            {\n                number = value.Substring(7);\n                if (number.Length != 3) return false;\n            }\n            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 0 || parsed > 200) return false;\n            result = (StudioTriggerEvent)parsed; return true;\n        }\n    }\n}\n`;
const files=new Map([
 ['metroidvania-studio/core/workspace/StudioTriggerEvent.generated.cs',csharp('MetroidvaniaStudio')],
 ['integrations/unity/Assets/metroidvania-studio-integration/runtime/StudioTriggerEvent.generated.cs',csharp('MetroidvaniaStudio.Integration')],
 ['metroidvania-studio/web/trigger-events.generated.ts',generated+`export const TRIGGER_EVENTS = ${JSON.stringify(names)} as const;\n`],
 ['integrations/shared/native/StudioTriggerEvent.generated.h',generated+`#pragma once\n#include <cstdint>\nnamespace MetroidvaniaStudio\n{\n    enum class TriggerEvent : uint8_t\n    {\n${names.map((n,i)=>`        ${n} = ${i}`).join(',\n')}\n    };\n}\n`],
 ['integrations/shared/unreal/public/MetroidvaniaStudioTriggerEvent.h',renderTriggerEvent(names,generated)],
 ['integrations/godot/addons/metroidvania-studio/trigger-event.gd',`# Generated from contracts/trigger-events.json.\nclass_name MetroidvaniaStudioTriggerEvent\nextends RefCounted\n\nenum Id {\n${names.map((n,i)=>`    ${n} = ${i}`).join(',\n')}\n}\n\nstatic func parse(value: String) -> int:\n    var source := value.strip_edges()\n    if source.is_empty() or source.to_lower() == "none": return Id.None\n    if source.to_lower().begins_with("trigger"):\n        source = source.substr(7)\n        if source.length() != 3: return Id.None\n    if source.is_empty(): return Id.None\n    for character in source:\n        if character < "0" or character > "9": return Id.None\n    var number := source.to_int()\n    return number if number >= 0 and number <= 200 else Id.None\n`]
]);
const core=readFileSync(path.join(root,'metroidvania-studio/core/workspace/StudioTriggerManager.cs'),'utf8').replaceAll('\r\n','\n');
files.set('integrations/unity/Assets/metroidvania-studio-integration/runtime/StudioTriggerManager.generated.cs',generated+core.replace('namespace MetroidvaniaStudio\n','namespace MetroidvaniaStudio.Integration\n'));
let stale=false;
for(const [relative,body] of files){const target=path.join(root,relative);let current='';try{current=readFileSync(target,'utf8').replaceAll('\r\n','\n');}catch{}if(current===body)continue;if(process.argv.includes('--check')){console.error('Stale trigger contract: '+relative);stale=true;}else writeFileSync(target,body);}
if(stale)process.exitCode=1;else console.log('Trigger event contracts are current (200 events, all engines).');
