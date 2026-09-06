#include "MetroidvaniaStudioRoom.h"
#include "Camera/CameraComponent.h"
#include "Engine/Texture2D.h"
#include "ImageUtils.h"
#include "TextureResource.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "ProceduralMeshComponent.h"
#include "UObject/ConstructorHelpers.h"
#include "StudioDocument.h"

namespace {
FString FromUtf8(const std::string& Text) { return FString(UTF8_TO_TCHAR(Text.c_str())); }
std::string ToUtf8(const FString& Text) { return std::string(TCHAR_TO_UTF8(*Text)); }
FVector Point(const MetroidvaniaStudio::Point& P, double Scale, double Depth=0) { return FVector(P.x*Scale,Depth,P.y*Scale); }
}
AMetroidvaniaStudioRoom::AMetroidvaniaStudioRoom()
{
    PrimaryActorTick.bCanEverTick = false;
    RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("RoomRoot"));
    RoomCamera = CreateDefaultSubobject<UCameraComponent>(TEXT("RoomCamera"));
    RoomCamera->SetupAttachment(RootComponent);
    RoomCamera->ProjectionMode = ECameraProjectionMode::Orthographic;
    RoomCamera->bConstrainAspectRatio = true;
    RoomCamera->SetRelativeRotation(FRotator(0,90,0));
    static ConstructorHelpers::FObjectFinder<UMaterialInterface> Material(TEXT("/Paper2D/MaskedUnlitSpriteMaterial.MaskedUnlitSpriteMaterial"));
    TileMaterial = Material.Object;
}
void AMetroidvaniaStudioRoom::DestroyGenerated()
{
    for(auto* Mesh : GeneratedMeshes) if(IsValid(Mesh)) Mesh->DestroyComponent();
    GeneratedMeshes.Reset(); bBuilt=false;
}
void AMetroidvaniaStudioRoom::ClearRoom()
{
    Modify(); DestroyGenerated(); MapDocumentJson.Empty(); CatalogJson.Empty(); Images.Empty(); LastError.Empty(); RoomName.Empty();
}
bool AMetroidvaniaStudioRoom::ImportRoom()
{
    try {
        const auto Map=MetroidvaniaStudio::ReadText(std::filesystem::u8path(ToUtf8(MapFile.FilePath)));
        const auto Catalog=MetroidvaniaStudio::ReadText(std::filesystem::u8path(ToUtf8(CatalogFile.FilePath)));
        const auto Room=MetroidvaniaStudio::LoadRoom(Map,Catalog,ToUtf8(RoomId));
        TMap<FString,FMetroidvaniaStudioImage> NewImages;
        for(const auto& P:Room.primitives) if(!P.sprite.asset.empty()) {
            const FString Key=FromUtf8(P.sprite.asset); if(NewImages.Contains(Key))continue;
            const auto Asset=MetroidvaniaStudio::ResourcePath(std::filesystem::u8path(ToUtf8(ResourceDirectory.Path)),P.sprite.asset);
            MetroidvaniaStudio::Require(std::filesystem::file_size(Asset)<=32*1024*1024,"Texture exceeds 32 MiB.");
            FMetroidvaniaStudioImage Image;
            MetroidvaniaStudio::Require(FFileHelper::LoadFileToArray(Image.Png,*FromUtf8(MetroidvaniaStudio::PathUtf8(Asset))),"Cannot read texture.");
            NewImages.Add(Key,MoveTemp(Image));
        }
        const FString OldMap=MapDocumentJson,OldCatalog=CatalogJson;const auto OldImages=Images;const FString OldId=RoomId;
        Modify(); MapDocumentJson=FromUtf8(Map);CatalogJson=FromUtf8(Catalog);Images=MoveTemp(NewImages);RoomId=FromUtf8(Room.id);
        if(!RebuildRoom()) {MapDocumentJson=OldMap;CatalogJson=OldCatalog;Images=OldImages;RoomId=OldId;return false;}
        // Persist imported content, not local source locations.
        MapFile.FilePath.Empty();CatalogFile.FilePath.Empty();ResourceDirectory.Path.Empty();return true;
    } catch(const std::exception& Error) {LastError=FromUtf8(Error.what());UE_LOG(LogTemp,Error,TEXT("MetroidvaniaStudio: %s"),*LastError);return false;}
}
bool AMetroidvaniaStudioRoom::RebuildRoom()
{
    try {
        MetroidvaniaStudio::Require(FMath::IsFinite(UnitsPerWorldUnit)&&UnitsPerWorldUnit>0,"Invalid world scale.");
        const auto Room=MetroidvaniaStudio::LoadRoom(ToUtf8(MapDocumentJson),ToUtf8(CatalogJson),ToUtf8(RoomId));
        const double Scale=UnitsPerWorldUnit/Room.ppu;
        TMap<FString,UTexture2D*> Textures;
        for(const auto& Entry:Images) {
            auto* Texture=FImageUtils::ImportBufferAsTexture2D(Entry.Value.Png);
            MetroidvaniaStudio::Require(Texture!=nullptr&&Texture->GetSizeX()<=16384&&Texture->GetSizeY()<=16384,"Invalid texture data.");
            Texture->Filter=TF_Nearest;Texture->SRGB=true;Texture->UpdateResource();Textures.Add(Entry.Key,Texture);
        }
        for(const auto& P:Room.primitives) if(!P.sprite.asset.empty()) {
            UTexture2D* const* Found=Textures.Find(FromUtf8(P.sprite.asset));MetroidvaniaStudio::Require(Found!=nullptr,"Missing embedded texture.");
            MetroidvaniaStudio::Require(static_cast<int64>(P.sprite.x)+P.sprite.width<=(*Found)->GetSizeX()&&static_cast<int64>(P.sprite.y)+P.sprite.height<=(*Found)->GetSizeY(),"Sprite rectangle is outside texture.");
        }
        MetroidvaniaStudio::Require(TileMaterial!=nullptr,"Paper2D sprite material is unavailable.");
        // Everything is validated before replacing the previous imported room.
        DestroyGenerated();
        struct Batch { TArray<FVector> Positions,Normals;TArray<int32> Triangles;TArray<FVector2D> UV;TArray<FLinearColor> Colors;TArray<TArray<FVector>> Collision;FString Asset;int Layer=0; };
        TMap<FString,Batch> Batches;
        auto NewMesh=[&]() {
            auto* Mesh=NewObject<UProceduralMeshComponent>(this,NAME_None,RF_Transient);
            Mesh->SetupAttachment(RootComponent);Mesh->bUseComplexAsSimpleCollision=false;Mesh->RegisterComponent();GeneratedMeshes.Add(Mesh);return Mesh;
        };
        size_t DrawIndex=0;
        for(const auto& P:Room.primitives) {
            const double DrawDepth=-P.layer-static_cast<double>(DrawIndex++)/(Room.primitives.size()+1);
            if(P.trigger) {
                auto* Trigger=NewMesh();TArray<FVector> Convex;
                for(const auto& V:P.points){Convex.Add(Point(V,Scale,-5));Convex.Add(Point(V,Scale,5));}
                Trigger->AddCollisionConvexMesh(Convex);Trigger->SetCollisionEnabled(ECollisionEnabled::QueryOnly);
                Trigger->SetCollisionResponseToAllChannels(ECR_Overlap);Trigger->SetGenerateOverlapEvents(true);
                Trigger->ComponentTags.Add(FName(*FromUtf8(P.objectId)));Trigger->ComponentTags.Add(FName(*FromUtf8(P.definition)));continue;
            }
            const FString Key=FString::FromInt(P.layer)+TEXT(":")+FromUtf8(P.sprite.asset);
            auto& Batch=Batches.FindOrAdd(Key);Batch.Asset=FromUtf8(P.sprite.asset);Batch.Layer=P.layer;
            const int32 Base=Batch.Positions.Num();auto* Texture=Textures.FindRef(Batch.Asset);
            for(size_t I=0;I<P.points.size();++I) {
                Batch.Positions.Add(Point(P.points[I],Scale,DrawDepth));Batch.Normals.Add(FVector(0,-1,0));
                Batch.Colors.Add(FLinearColor(P.color.r,P.color.g,P.color.b,P.color.a));
                Batch.UV.Add(Texture?FVector2D((P.sprite.x+P.uv[I].x*P.sprite.width)/Texture->GetSizeX(),1.0-(P.sprite.y+P.uv[I].y*P.sprite.height)/Texture->GetSizeY()):FVector2D::ZeroVector);
            }
            for(int32 I=1;I<static_cast<int32>(P.points.size())-1;++I){Batch.Triangles.Add(Base);Batch.Triangles.Add(Base+I);Batch.Triangles.Add(Base+I+1);}
            if(P.solid) {TArray<FVector> Convex;for(const auto& V:P.points){Convex.Add(Point(V,Scale,-5));Convex.Add(Point(V,Scale,5));}Batch.Collision.Add(MoveTemp(Convex));}
        }
        for(auto& Entry:Batches) {
            auto& B=Entry.Value;auto* Mesh=NewMesh();
            Mesh->CreateMeshSection_LinearColor(0,B.Positions,B.Triangles,B.Normals,B.UV,B.Colors,TArray<FProcMeshTangent>(),false);
            auto* Material=UMaterialInstanceDynamic::Create(TileMaterial,Mesh);
            auto* Texture=Textures.FindRef(B.Asset);
            if(!Texture) {
                Texture=UTexture2D::CreateTransient(1,1,PF_B8G8R8A8);
                auto& Mip=Texture->GetPlatformData()->Mips[0];auto* Pixels=static_cast<uint32*>(Mip.BulkData.Lock(LOCK_READ_WRITE));*Pixels=0xffffffff;Mip.BulkData.Unlock();Texture->UpdateResource();
            }
            Material->SetTextureParameterValue(TEXT("SpriteTexture"),Texture);Mesh->SetMaterial(0,Material);
            if(B.Collision.Num()) {Mesh->SetCollisionConvexMeshes(B.Collision);Mesh->SetCollisionEnabled(ECollisionEnabled::QueryAndPhysics);Mesh->SetCollisionResponseToAllChannels(ECR_Block);}
            else Mesh->SetCollisionEnabled(ECollisionEnabled::NoCollision);
        }
        SetActorLocation(FVector(static_cast<double>(Room.x)*16*Scale,GetActorLocation().Y,static_cast<double>(Room.y)*16*Scale));
        PixelsPerUnit=Room.ppu;ReferenceResolution=FIntPoint(Room.referenceWidth,Room.referenceHeight);RoomName=FromUtf8(Room.name);
        RoomCamera->OrthoWidth=Room.referenceWidth*Scale;RoomCamera->AspectRatio=static_cast<float>(Room.referenceWidth)/Room.referenceHeight;
        RoomCamera->SetRelativeLocation(FVector(Room.width*8*Scale,-1000,Room.height*8*Scale));
        LastError.Empty();bBuilt=true;return true;
    } catch(const std::exception& Error){LastError=FromUtf8(Error.what());UE_LOG(LogTemp,Error,TEXT("MetroidvaniaStudio: %s"),*LastError);return false;}
}
void AMetroidvaniaStudioRoom::OnConstruction(const FTransform& Transform) {Super::OnConstruction(Transform);if(!MapDocumentJson.IsEmpty())RebuildRoom();}
void AMetroidvaniaStudioRoom::BeginPlay() {Super::BeginPlay();if(!bBuilt&&!MapDocumentJson.IsEmpty())RebuildRoom();}
