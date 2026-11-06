// Drive the real pose-update detour with a fake original, without starting SteamVR.
// There is no shared memory here (state stays null), so updated() runs on `command`.
#include "Driver.cpp"
#include <cstdlib>
namespace {
void require(bool condition) {if(!condition) std::abort();}
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
}
