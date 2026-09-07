using UnrealBuildTool;

public class MetroidvaniaStudio : ModuleRules
{
    public MetroidvaniaStudio(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Default;
        bEnableExceptions = true;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "ProceduralMeshComponent", "UMG", "SlateCore" });
        PrivateDependencyModuleNames.AddRange(new[] { "Projects", "Paper2D", "Slate", "RenderCore" });
    }
}
