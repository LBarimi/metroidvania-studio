export function renderTriggerEvent(names, generated) {
  return generated+`#pragma once\n#include "CoreMinimal.h"\n#include "MetroidvaniaStudioTriggerEvent.generated.h"\n\nUENUM(BlueprintType)\nenum class EStudioTriggerEvent : uint8\n{\n${names.map((n,i)=>`    ${n} = ${i}`).join(',\n')}\n};\n`;
}
