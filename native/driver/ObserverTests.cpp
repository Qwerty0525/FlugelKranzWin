// Include the actual callbacks to test forwarding without starting SteamVR.
#include "Driver.cpp"
#include <cstdlib>
#include <limits>
namespace {
const Layer* expectedEyes=nullptr;
Layer expectedCopy[2]{};
unsigned forwarded=0;
bool expectCopy=false;
void* expectedSelf=reinterpret_cast<void*>(0x1234);
void require(bool condition) {if(!condition) std::abort();}
void forwardedSubmit(void* self,const Layer(&eyes)[2]) {
    require(self==expectedSelf);
    require((&eyes[0]!=expectedEyes)==expectCopy);
    require(std::memcmp(eyes,expectedCopy,sizeof(expectedCopy))==0);
    forwarded++;
}
void* componentResult=nullptr;
void* forwardedComponent(void* self,const char*) {
    require(self==expectedSelf);
    forwarded++;
    return componentResult;
}
}
int main() {
    Layer eyes[2]{};
    eyes[0].hTexture=12;eyes[1].hTexture=34;
    eyes[0].mHmdPose.m[0][0]=1;
    eyes[1].mHmdPose.m[1][2]=0.6f;
    eyes[0].flHmdPosePredictionTimeInSecondsFromNow=0.013f;
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    expectedEyes=eyes;
    submitOriginal=forwardedSubmit;
    // Logging disabled here: verifies the production forwarding path in isolation.
    nextFrameLog=std::numeric_limits<uint64_t>::max();
    driverAudit=true; // counts each layer's path; the line itself is logged from RunFrame
    observedSubmit(expectedSelf,eyes);
    require(forwarded==1 && inFlight==0);
    require(std::memcmp(eyes,expectedCopy,sizeof(eyes))==0);
    componentOriginal=forwardedComponent;
    require(observedComponent(expectedSelf,vr::IVRDriverDirectModeComponent_Version)==nullptr);
    componentResult=reinterpret_cast<void*>(0x5678);
    require(observedComponent(expectedSelf,"IVRDriverDirectModeComponent_008")==componentResult);
    require(submitTarget==nullptr && componentTarget==nullptr);
    require(forwarded==3 && inFlight==0);
    vr::DriverPose_t head{};
    head.qRotation.w=head.qWorldFromDriverRotation.w=head.qDriverFromHeadRotation.w=1;
    head.poseIsValid=head.deviceIsConnected=true;
    head.vecPosition[0]=1;
    flight::Pose transform{0,std::sqrt(0.5),0,std::sqrt(0.5),3,0,0};
    auto output=flight::apply(head,transform);
    for(auto& eye:eyes) {
        eye.mHmdPose=flight::matrix(flight::physical(output));
        eye.flHmdPosePredictionTimeInSecondsFromNow=0.02f;
        eye.hDepthTexture=56;eye.bounds.uMin=0.2f;eye.mProjection.m[2][2]=0.75f;
    }
    Layer originalCopy[2];std::memcpy(originalCopy,eyes,sizeof(eyes));
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    for(auto& eye:expectedCopy) eye.mHmdPose=flight::matrix(flight::physical(head));
    // Float rounding after inverse multiplication is checked separately; retain
    // the full byte-level check for all fields outside the pose matrix.
    for(int i=0;i<2;i++) expectedCopy[i].mHmdPose=flight::removeTransform(eyes[i].mHmdPose,transform);
    correctFramePose=true;expectCopy=true;
    frameHistory.add(frameTimeMs(),output,transform);
    observedSubmit(expectedSelf,eyes);
    require(forwarded==4 && inFlight==0);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    frameHistory.clear();expectCopy=false;
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    observedSubmit(expectedSelf,eyes);
    require(forwarded==5 && inFlight==0);
    timeBasedFramePose=true;expectCopy=true;
    frameHistory.add(frameTimeMs(),output,transform,&head);
    for(auto& eye:expectedCopy) eye.mHmdPose=flight::matrix(flight::physical(head));
    observedSubmit(expectedSelf,eyes);
    require(forwarded==6 && inFlight==0);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    // Identity must preserve even a render pose that differs from our own
    // prediction: forward the exact original pointer and bytes.
    frameHistory.clear();
    const double resetNow=frameTimeMs();
    for(int dt=300;dt>=0;dt-=10) frameHistory.add(resetNow-dt,head,flight::Pose{},&head);
    expectCopy=false;
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    auto missedBefore=missedLayers.load();
    observedSubmit(expectedSelf,eyes);
    require(forwarded==7 && inFlight==0);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    require(missedLayers.load()==missedBefore);
    // Stable flight keeps the HMD driver's render prediction, even when it
    // disagrees with the independent physical prediction in our history.
    frameHistory.clear();
    head.vecVelocity[0]=5;
    const double heldNow=frameTimeMs();
    for(int dt=300;dt>=0;dt-=10) frameHistory.add(heldNow-dt,output,transform,&head);
    for(int i=0;i<2;i++) expectedCopy[i].mHmdPose=flight::removeTransform(eyes[i].mHmdPose,transform);
    expectCopy=true;
    auto heldBefore=heldLayers.load();
    observedSubmit(expectedSelf,eyes);
    require(forwarded==8 && inFlight==0);
    require(heldLayers.load()==heldBefore+1);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    // Every layer above took a different path once, in this order: forwarded (correction
    // off), matched, missed, timed, identity bypass, held.
    for(int path=0;path<PathFallback;path++) require(audit.paths[path]==1);
    require(audit.paths[PathFallback]==0 && audit.pathChanges==5 && audit.pathJumpMax>0);

    // fallbackFramePose. A stale history defeats every path above; the layer was flown
    // with the newest transform, so removing it gives the physical head.
    fallbackFramePose=true;
    frameHistory.clear();
    const double staleNow=frameTimeMs();
    for(int dt=400;dt>=200;dt-=10) frameHistory.add(staleNow-dt,output,transform,&head);
    for(int i=0;i<2;i++) expectedCopy[i].mHmdPose=flight::removeTransform(eyes[i].mHmdPose,transform);
    expectCopy=true;
    observedSubmit(expectedSelf,eyes);
    require(forwarded==9 && audit.paths[PathFallback]==1);
    require(flight::matrixError(expectedCopy[0].mHmdPose,flight::matrix(flight::physical(head)))<1e-6);
    // Guard: flight has just started, so the newest transform is not identity, but this
    // layer was rendered before it (with identity). It is forwarded as it is.
    frameHistory.clear();
    vr::DriverPose_t still=head;still.vecVelocity[0]=0;
    for(int dt=400;dt>210;dt-=10) frameHistory.add(staleNow-dt,still,flight::Pose{},&still);
    frameHistory.add(staleNow-200,flight::apply(still,transform),transform,&still);
    Layer unflown[2];std::memcpy(unflown,eyes,sizeof(eyes));
    for(auto& eye:unflown) eye.mHmdPose=flight::matrix(flight::physical(still));
    std::memcpy(expectedCopy,unflown,sizeof(unflown));
    expectedEyes=unflown;expectCopy=false;
    observedSubmit(expectedSelf,unflown);
    require(forwarded==10 && audit.paths[PathFallback]==1 && audit.paths[PathMissed]==2);
    expectedEyes=eyes;
    // No history at all: the HMD's last committed transform.
    frameHistory.clear();
    {std::lock_guard<std::mutex> lock(appliedGate);lastApplied[0]=transform;}
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    for(int i=0;i<2;i++) expectedCopy[i].mHmdPose=flight::removeTransform(eyes[i].mHmdPose,transform);
    expectCopy=true;
    observedSubmit(expectedSelf,eyes);
    require(forwarded==11 && audit.paths[PathFallback]==2);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    // Without correctFramePose there is no fallback either.
    correctFramePose=false;expectCopy=false;
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    observedSubmit(expectedSelf,eyes);
    require(forwarded==12 && audit.paths[PathFallback]==2);
    {std::lock_guard<std::mutex> lock(appliedGate);lastApplied[0]=flight::Pose{};}
    fallbackFramePose=false;
    std::puts("Observer forwards original layers and ignores unavailable/unsupported interfaces; fallback correction and its guard: passed");
}
