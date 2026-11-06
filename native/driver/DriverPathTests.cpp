// Drive the real pose-update detour with a fake original, without starting SteamVR.
// There is no shared memory here (state stays null), so updated() runs on `command`.
#include "Driver.cpp"
#include "DriverHarness.h"
#include <cstdlib>
namespace {
void requireAt(bool condition,int line) {if(!condition) {std::fprintf(stderr,"DriverPathTests.cpp:%d: check failed\n",line);std::abort();}}
#define require(condition) requireAt((condition),__LINE__)
bool same(flight::Pose a,flight::Pose b) {
    return a.x==b.x && a.y==b.y && a.z==b.z && a.w==b.w && a.px==b.px && a.py==b.py && a.pz==b.pz;
}
void* expectedHost=reinterpret_cast<void*>(0x1234);
vr::DriverPose_t forwardedPose{};
uint32_t forwardedDevice=0,forwardedSize=0;
unsigned forwardedCount=0;
flight::Pose expectedPending,expectedCommitted;
void fakeOriginal(void* host,uint32_t device,const vr::DriverPose_t& pose,uint32_t size) {
    require(host==expectedHost);
    forwardedPose=pose;forwardedDevice=device;forwardedSize=size;forwardedCount++;
    if(size!=sizeof(vr::DriverPose_t) || device>=lastApplied.size()) return;
    // The readback must see the new transform as pending, not yet committed, while
    // vrserver may still hold the previous pose.
    std::lock_guard<std::mutex> lock(appliedGate);
    require(forwarding[device]);
    require(same(pendingApplied[device],expectedPending));
    require(same(lastApplied[device],expectedCommitted));
}
void keepCommand(flight::Pose transform) {
    command=transform;
    lastHeartbeat=GetTickCount64(); // refresh() drops a command older than 500 ms
}
// What vrserver holds for the readback: the last pose forwarded per device.
std::array<vr::TrackedDevicePose_t,16> serverPoses{};
vr::TrackedDevicePose_t tracked(const vr::DriverPose_t& pose) {
    vr::TrackedDevicePose_t result{};
    result.mDeviceToAbsoluteTracking=flight::matrix(flight::physical(pose));
    for(int i=0;i<3;i++) {result.vVelocity.v[i]=static_cast<float>(pose.vecVelocity[i]);result.vAngularVelocity.v[i]=static_cast<float>(pose.vecAngularVelocity[i]);}
    result.bPoseIsValid=pose.poseIsValid;result.bDeviceIsConnected=pose.deviceIsConnected;
    result.eTrackingResult=vr::TrackingResult_Running_OK;
    return result;
}
void storingOriginal(void* host,uint32_t device,const vr::DriverPose_t& pose,uint32_t size) {
    require(host==expectedHost);
    forwardedPose=pose;forwardedDevice=device;forwardedSize=size;forwardedCount++;
    if(device<serverPoses.size()) serverPoses[device]=tracked(pose);
}
void serverReadback(void*,float,vr::TrackedDevicePose_t* poses,uint32_t count) {
    for(uint32_t i=0;i<count && i<serverPoses.size();i++) poses[i]=serverPoses[i];
}
double positionError(const vr::HmdMatrix34_t& a,const vr::HmdMatrix34_t& b) {
    double sum=0;for(int row=0;row<3;row++) {const double d=a.m[row][3]-b.m[row][3];sum+=d*d;}
    return std::sqrt(sum);
}

// P0: the harness itself. Settings, device properties, the IPC mutex and the readback
// caller check all run through the production code paths.
void harnessTests(const vr::DriverPose_t& tracker,uint32_t device) {
    harness::install();
    auto& context=harness::context();

    // Settings: shipped defaults, a changed flag, and missing keys keeping the fallbacks.
    harness::shippedDefaults();
    loadSettings();
    require(context.log.contains("FlugelKranz flags: observeFrames=1 correctFramePose=1 timeBasedFramePose=1 driverAudit=1 fallbackFramePose=0"));
    require(std::strcmp(physicalPoseDrivers,"driver_slimevr.dll,driver_rebocap.dll")==0);
    context.settings.bools["timeBasedFramePose"]=false;
    context.log.clear();
    loadSettings();
    require(context.log.contains("FlugelKranz flags: observeFrames=1 correctFramePose=1 timeBasedFramePose=0"));
    context.settings.bools.clear();context.settings.strings.clear();
    context.log.clear();
    loadSettings();
    require(context.log.contains("FlugelKranz flags: observeFrames=0 correctFramePose=0 timeBasedFramePose=0 driverAudit=0"));
    require(std::strcmp(physicalPoseDrivers,"driver_slimevr.dll,driver_rebocap.dll")==0);
    observeFrames=correctFramePose=timeBasedFramePose=false;

    // Device properties: only a listed tracking system is anchored, and the answer is cached.
    std::snprintf(hmdAnchoredSystems,sizeof(hmdAnchoredSystems),"%s","SlimeVR");
    context.properties.trackingSystems={{7,"slimevr"},{8,"lighthouse"}};
    propertiesReady=true;
    require(classify(7)==2 && context.log.contains("device 7 (slimevr) follows the flown HMD"));
    require(classify(8)==1 && classify(9)==1);
    context.properties.trackingSystems.clear();
    require(classify(7)==2);
    hmdAnchoredSystems[0]=0;

    // IPC: a fresh valid command is taken; a paused app keeps it without blocking.
    harness::useIpc();
    const flight::Pose flight{0,std::sqrt(0.5),0,std::sqrt(0.5),3,-1,2};
    harness::publish(flight);
    {std::lock_guard<std::mutex> lock(gate);refresh();}
    require(same(command,flight) && lastHeartbeat!=0);
    originals[0]=storingOriginal;
    {
        harness::AppHoldsIpc paused;
        const auto before=state->samples[device].timestamp;
        const auto start=std::chrono::steady_clock::now();
        updated(0,expectedHost,device,tracker,sizeof(tracker));
        require(std::chrono::steady_clock::now()-start<std::chrono::milliseconds(50));
        require(state->samples[device].timestamp==before); // the sample is skipped, not waited for
        const auto expected=flight::apply(tracker,flight);
        require(std::memcmp(&forwardedPose,&expected,sizeof(expected))==0);
    }
    updated(0,expectedHost,device,tracker,sizeof(tracker));
    require(state->samples[device].timestamp!=0 && (state->samples[device].flags&3u)==3u);
    // An invalid command or a dead client drops to identity at once.
    harness::publish(flight::Pose{0,0,0,2,0,0,0});
    {std::lock_guard<std::mutex> lock(gate);refresh();}
    require(flight::isIdentity(command) && lastHeartbeat==0);
    harness::publish(flight);
    WaitForSingleObject(ipcMutex,INFINITE);state->clientHeartbeat=GetTickCount64()-600;ReleaseMutex(ipcMutex);
    {std::lock_guard<std::mutex> lock(gate);refresh();}
    require(flight::isIdentity(command) && state->owner==0 && state->enabled==0);

    // Readback: a listed caller gets the flight removed; anyone else gets vrserver's pose.
    harness::publish(flight);
    updated(0,expectedHost,device,tracker,sizeof(tracker));
    require(same(lastApplied[device],flight));
    rawOriginal=serverReadback;
    harness::servePhysicalPosesToThisProcess();
    std::array<vr::TrackedDevicePose_t,16> poses{};
    rawPoses(expectedHost,0,poses.data(),static_cast<uint32_t>(poses.size()));
    require(positionError(poses[device].mDeviceToAbsoluteTracking,flight::matrix(flight::physical(tracker)))<1e-5);
    require(context.log.contains(("raw poses for "+harness::executableName()+": physical").c_str()));
    harness::servePhysicalPosesToThisProcess(false);
    rawPoses(expectedHost,0,poses.data(),static_cast<uint32_t>(poses.size()));
    require(std::memcmp(&poses[device],&serverPoses[device],sizeof(poses[device]))==0);
    std::puts("harness: settings and flags line, device properties, IPC take/miss/invalid/revoke, readback caller: passed");
}

vr::TrackedDevicePose_t readbackDuringForward{};
uint32_t readbackDevice=0;
void readingOriginal(void* host,uint32_t device,const vr::DriverPose_t& pose,uint32_t size) {
    // A solver polls while this pose is on its way: vrserver still holds the previous one.
    std::array<vr::TrackedDevicePose_t,16> poses{};
    rawPoses(expectedHost,0,poses.data(),static_cast<uint32_t>(poses.size()));
    readbackDuringForward=poses[readbackDevice];
    storingOriginal(host,device,pose,size);
}
Audit auditNow() {std::lock_guard<std::mutex> lock(auditGate);return audit;}

// P1a: DriverAudit counters and the 1 Hz line.
void auditTests(const vr::DriverPose_t& tracker,uint32_t device) {
    auto& context=harness::context();
    const flight::Pose a{0,std::sqrt(0.5),0,std::sqrt(0.5),3,-1,2},b{0,0,0,1,1,0,0};
    require(intervalBucket(4.9)==0 && intervalBucket(5)==1 && intervalBucket(99)==6 && intervalBucket(100)==7);

    // Off: nothing is counted.
    driverAudit=false;
    {std::lock_guard<std::mutex> lock(auditGate);audit=Audit{};lastCommandChange=0;}
    {harness::AppHoldsIpc paused;updated(0,expectedHost,device,tracker,sizeof(tracker));}
    require(auditNow().refreshMiss==0 && auditNow().sampleMiss==0);

    driverAudit=true;
    harness::publish(b);updated(0,expectedHost,device,tracker,sizeof(tracker));
    require(auditNow().commandChanges==1);
    harness::publish(a);updated(0,expectedHost,device,tracker,sizeof(tracker));
    {
        const auto counted=auditNow();
        uint64_t intervals=0;for(auto n:counted.commandInterval) intervals+=n;
        require(counted.commandChanges==2 && intervals==1);
    }
    {harness::AppHoldsIpc paused;updated(0,expectedHost,device,tracker,sizeof(tracker));}
    require(auditNow().refreshMiss==1 && auditNow().sampleMiss==1);
    // An invalid command is told apart from a dead client.
    harness::publish(flight::Pose{0,0,0,2,0,0,0});
    {std::lock_guard<std::mutex> lock(gate);refresh();}
    require(auditNow().commandInvalid==1 && auditNow().ownerRevoked==0);
    harness::publish(a);
    WaitForSingleObject(ipcMutex,INFINITE);state->clientHeartbeat=GetTickCount64()-600;ReleaseMutex(ipcMutex);
    {std::lock_guard<std::mutex> lock(gate);refresh();}
    require(auditNow().commandInvalid==1 && auditNow().ownerRevoked==1);

    // The HMD's virtual step between consecutive commands.
    harness::publish(a);updated(0,expectedHost,0,tracker,sizeof(tracker));
    harness::publish(b);updated(0,expectedHost,0,tracker,sizeof(tracker));
    {
        const auto head=flight::physical(tracker);
        const double h[3]{head.px,head.py,head.pz};double viaA[3],viaB[3];
        flight::transformPoint(a,h,viaA);flight::transformPoint(b,h,viaB);
        const double expected=std::sqrt((viaA[0]-viaB[0])*(viaA[0]-viaB[0])+(viaA[1]-viaB[1])*(viaA[1]-viaB[1])+(viaA[2]-viaB[2])*(viaA[2]-viaB[2]));
        const auto counted=auditNow();
        require(counted.virtualSteps==2 && std::abs(counted.virtualStepMax-expected)<1e-9);
    }

    // A readback while this device's pose is being forwarded picks the previous transform.
    harness::servePhysicalPosesToThisProcess();
    require(same(lastApplied[device],a));
    originals[0]=readingOriginal;readbackDevice=device;
    updated(0,expectedHost,device,tracker,sizeof(tracker)); // forwards b
    originals[0]=storingOriginal;
    require(positionError(readbackDuringForward.mDeviceToAbsoluteTracking,flight::matrix(flight::physical(tracker)))<1e-5);
    {
        const auto counted=auditNow();
        require(counted.readbackCalls==1 && counted.readbackWindow==1 && counted.pickedPrevious==1
            && counted.pickedPending==0 && counted.ambiguous==0 && counted.readbackResidualMax<1e-5);
    }

    // The line: emitted at most once per second, outside the locks, and the counters reset.
    context.log.clear();
    {std::lock_guard<std::mutex> lock(auditGate);nextAudit=0;}
    auditTick(frameTimeMs());
    const auto line=context.log.last("DriverAudit t=");
    require(line.find("refreshMiss=1 sampleMiss=1 ownerRevoked=1 commandInvalid=1")!=std::string::npos);
    require(line.find("readback: calls=1 window=1 pending=0 previous=1 ambiguous=0")!=std::string::npos);
    require(!context.log.contains("truncated"));
    require(auditNow().commandChanges==0 && auditNow().readbackCalls==0);
    auditTick(frameTimeMs());
    require(context.log.count("DriverAudit t=")==1);

    // Direct Mode: reported once, from the missing SubmitLayer hook, 10 s after Init.
    observeFrames=true;
    {std::lock_guard<std::mutex> lock(auditGate);nextAudit=0;auditStart=frameTimeMs()-11000;directModeReported=false;}
    auditTick(frameTimeMs());
    require(context.log.count("render correction OFF")==1 && context.log.contains("no HMD GetComponent observer"));
    {std::lock_guard<std::mutex> lock(auditGate);nextAudit=0;}
    auditTick(frameTimeMs());
    require(context.log.count("render correction OFF")==1);
    observeFrames=false;
    std::puts("audit: off, command changes/intervals, IPC misses, invalid vs revoked, virtual step, readback window, line and reset, Direct Mode report: passed");
}
}
int main() {
    originals[0]=fakeOriginal;
    vr::DriverPose_t tracker{};
    tracker.qRotation={std::cos(0.2),0,std::sin(0.2),0};
    tracker.qWorldFromDriverRotation.w=tracker.qDriverFromHeadRotation.w=1;
    tracker.vecPosition[0]=0.3;tracker.vecPosition[1]=1.1;tracker.vecPosition[2]=-0.4;
    tracker.poseIsValid=tracker.deviceIsConnected=true;
    const uint32_t device=5;

    // Identity: the original pose is forwarded byte for byte.
    keepCommand(flight::Pose{});
    expectedPending=expectedCommitted=flight::Pose{};
    updated(0,expectedHost,device,tracker,sizeof(tracker));
    require(forwardedCount==1 && forwardedDevice==device && forwardedSize==sizeof(tracker));
    require(std::memcmp(&forwardedPose,&tracker,sizeof(tracker))==0);
    require(!forwarding[device] && same(lastApplied[device],flight::Pose{}) && inFlight==0);

    // Flight: the forwarded pose carries the command, which is committed only after
    // the original returns.
    const flight::Pose flight{0,std::sqrt(0.5),0,std::sqrt(0.5),3,-1,2};
    keepCommand(flight);
    expectedPending=flight;expectedCommitted=flight::Pose{};
    updated(0,expectedHost,device,tracker,sizeof(tracker));
    const auto expected=flight::apply(tracker,flight);
    require(forwardedCount==2 && std::memcmp(&forwardedPose,&expected,sizeof(expected))==0);
    require(!forwarding[device] && same(lastApplied[device],flight) && inFlight==0);
    const auto body=flight::physical(tracker);
    require(pendingPhysical[device][0]==body.px && pendingPhysical[device][1]==body.py
        && pendingPhysical[device][2]==body.pz);

    // A stale command (no heartbeat for 500 ms) falls back to identity.
    command=flight;lastHeartbeat=GetTickCount64()-600;
    expectedPending=flight::Pose{};expectedCommitted=flight;
    updated(0,expectedHost,device,tracker,sizeof(tracker));
    require(forwardedCount==3 && std::memcmp(&forwardedPose,&tracker,sizeof(tracker))==0);
    require(same(lastApplied[device],flight::Pose{}));

    // An unknown ABI size is passed through untouched and records nothing.
    keepCommand(flight);
    updated(0,expectedHost,device,tracker,sizeof(tracker)-4);
    require(forwardedCount==4 && forwardedSize==sizeof(tracker)-4);
    require(std::memcmp(&forwardedPose,&tracker,sizeof(tracker))==0);
    require(same(lastApplied[device],flight::Pose{}) && inFlight==0);

    std::puts("pose-update detour: identity pass-through, flight, pending/commit order, stale command, unknown ABI: passed");

    harnessTests(tracker,device);
    auditTests(tracker,device);
}
