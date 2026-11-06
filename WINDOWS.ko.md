<!-- Language switch: one badge per line (they render side by side). Keep the same list in every WINDOWS*.md; the current language uses the darker color. -->
[![日本語](https://img.shields.io/badge/lang-%E6%97%A5%E6%9C%AC%E8%AA%9E-e05d44)](WINDOWS.md)
[![English](https://img.shields.io/badge/lang-English-007ec6)](WINDOWS.en.md)
[![한국어](https://img.shields.io/badge/lang-%ED%95%9C%EA%B5%AD%EC%96%B4-1a5c2e)](WINDOWS.ko.md)

# FlugelKranzWin: 설치·개발 가이드

이 저장소의 대상은 Windows x64 / SteamVR입니다. Linux / Monado / WiVRn 판은 [상위 저장소](https://github.com/ReinaS-64892/FlugelKranz)를 참고해 주세요. 조작과 관성의 사양은 [README](README.ko.md)에 정리해 두었습니다.

## 필요한 환경

- Windows x64, SteamVR, SteamVR로 추적할 수 있는 HMD와 양손 컨트롤러.
- 이 저장소의 전용 SteamVR 드라이버.
- 빌드할 때: .NET 10 SDK, MSVC x64 Build Tools, Windows SDK, CMake.

배포 앱은 self-contained이므로, 실행만 할 때는 .NET SDK를 따로 설치할 필요가 없습니다. 현재 실제 기기에서 확인한 구성은 Quest 2 + Touch / Virtual Desktop입니다.

## 릴리스 zip으로 설치

빌드하지 않고 쓰려면 [Releases](https://github.com/Qwerty0525/FlugelKranzWin/releases)에서 `FlugelKranzWin-<버전>-win-x64.zip`(예: `FlugelKranzWin-v0.9.0-win-x64.zip`)을 받습니다. 안에는 `FlugelKranzWin` 폴더 하나가 들어 있으며, 아래 빌드 출력과 같은 구성(`FlugelKranz.exe`, `driver/flugelkranz`, `install-driver.ps1`)의 릴리스 빌드이며, 라이선스와 문서도 들어 있습니다. .NET SDK나 CMake는 필요 없습니다. 드라이버 설정 `default.vrsettings`는 아래 [프레임 자세 보정](#프레임-자세-보정-설정)의 확인된 설정으로 되어 있으므로 고치지 않고 쓸 수 있습니다.

1. SteamVR을 종료합니다.
2. zip의 압축을 모두 풀고, `FlugelKranzWin` 폴더를 계속 둘 곳에 놓습니다. 드라이버는 이 폴더의 위치로 등록되므로 zip 안에서 바로 실행하지 마세요.
3. 예전 버전이나 상위 저장소의 빌드 등, 다른 폴더에서 FlugelKranz 드라이버를 이미 등록했다면 그 폴더에서 `./install-driver.ps1 -Uninstall`을 실행해 등록이 두 곳에 생기지 않게 합니다.
4. `FlugelKranzWin` 폴더에서 PowerShell을 열고(폴더를 오른쪽 클릭 → "터미널에서 열기") 드라이버를 등록합니다.
5. SteamVR을 실행하고 HMD와 양손이 추적되는지 확인한 뒤, 진단하고 앱을 실행합니다. 그다음은 README의 [처음 조작](README.ko.md#처음-조작)을 참고해 주세요.

```powershell
powershell -ExecutionPolicy Bypass -File ./install-driver.ps1
# SteamVR을 실행한 뒤:
./FlugelKranz.exe --diagnose | Out-Host
./FlugelKranz.exe
```

`-ExecutionPolicy Bypass`는 이번 한 번의 실행에만 스크립트를 허용하며, Windows 설정은 바꾸지 않습니다. 스크립트를 실행할 수 있는 환경이라면 `./install-driver.ps1`로 실행해도 같습니다. 파일 탐색기에서 `FlugelKranz.exe`를 열 때 Windows SmartScreen 경고가 나오면 "추가 정보" → "실행"을 고릅니다. F 모드의 머리 조종(기본 켜짐)에는 SteamVR 개발자 설정 **Experimental overlay input overrides**가 필요합니다([README](README.ko.md#머리-조종-시험-기능windows--f-모드) 참고).

업데이트할 때는 FlugelKranz와 SteamVR을 종료하고, 새 `FlugelKranzWin` 폴더의 내용을 지금 쓰는 폴더에 덮어씁니다(기존 등록이 그대로 쓰입니다). `default.vrsettings`를 고쳤다면 새 파일로 바뀌므로 다시 확인해 주세요. 다른 폴더로 옮길 때는 [업데이트와 등록 해제](#업데이트와-등록-해제)대로 등록을 옮깁니다.

## 빌드와 첫 설치

저장소 루트에서 실행합니다.

```powershell
./build-windows.ps1
# PATH에 없을 때:
# ./build-windows.ps1 -Dotnet 'C:/path/to/dotnet.exe' -CMake 'C:/path/to/cmake.exe'
# 비행·나침반 시험용 빌드(driverAudit = true, 나침반 기본 켜짐):
# ./build-windows.ps1 -TestBuild
# 배치 폴더(artifacts/windows-x64) 말고 다른 곳으로 출력(배포 zip 만들기 등):
# ./build-windows.ps1 -OutputDir artifacts/release-v0.9.0
```

일반(릴리스) 빌드는 배치할 드라이버 설정 `default.vrsettings`에 `driverAudit: false`를, `-TestBuild`를 붙인 빌드는 `driverAudit: true`를 씁니다(초마다 `DriverAudit` 줄을 `vrserver.txt`에 출력). 둘 다 빌드할 때마다 배치 폴더의 값을 덮어쓰므로, 앱의 "Detailed diagnostics"(「詳細診断」)에서 바꾼 값도 빌드하면 되돌아갑니다.
`-TestBuild`는 앱도 시험용 프로필(`FlugelKranzBuildProfile=test`, FlugelKranz.dll의 어셈블리 메타데이터)로 빌드하며, 아직 저장되지 않은 설정에서는 중력 나침반을 켠 상태로 시작합니다. 릴리스 빌드에서는 꺼져 있습니다. `config.json`에 저장된 값은 어느 빌드에서든 그대로 쓰입니다.

출력 폴더는 `artifacts/windows-x64`입니다. 폴더 전체를 배치하고, 배치할 곳을 정한 뒤 등록해 주세요. 앱 이름은 `FlugelKranz`, 실행 파일은 `FlugelKranz.exe`입니다.

1. SteamVR을 종료합니다.
2. 아래 프레임 자세 보정 설정을 배치한 드라이버의 설정 파일에 반영합니다.
3. 배포 폴더에서 등록 스크립트를 실행합니다.
4. SteamVR을 실행하고 HMD와 양손의 추적을 확인합니다.
5. 진단한 뒤 앱을 실행합니다.

```powershell
cd artifacts/windows-x64
./install-driver.ps1
# 설정과 등록을 마치고 SteamVR을 실행한 뒤:
./FlugelKranz.exe --diagnose | Out-Host
./FlugelKranz.exe
```

등록 스크립트는 SteamVR의 `vrpathreg`를 사용합니다. SteamVR을 종료하거나 다시 시작하지 않으며, 자동 실행 설정도 추가하지 않습니다. SteamVR의 애드온 관리에서 드라이버를 꺼 두었다면 켜 주세요.

## 프레임 자세 보정 설정

배치 폴더의 `driver/flugelkranz/resources/settings/default.vrsettings`를 SteamVR을 실행하기 전에 설정합니다. 실제 기기에서 검은 테두리·화면 어두워짐이 개선된 것을 확인한 구성은 다음과 같습니다.

```json
{
  "driver_flugelkranz": {
    "loadPriority": 100,
    "observeFrames": true,
    "correctFramePose": true,
    "timeBasedFramePose": true
  }
}
```

**소스와 배포판의 기본값은 실제 기기에서 확인한 위 3개의 boolean 설정이 모두 true입니다.** 빌드 스크립트는 소스의 설정을 배치 폴더로 복사하므로, 다시 빌드하거나 업데이트한 뒤에도 확인해 주세요. SteamVR에 같은 이름의 사용자 설정이 있으면 그쪽이 우선합니다.

`correctFramePose`는 렌더링·스트리밍에 넘기는 HMD 자세의 보정을 켭니다. `timeBasedFramePose`는 프레임의 예측 시각에 해당하는 물리 자세를 보간·예측하는 방식으로, `correctFramePose=true`와 함께 씁니다. `observeFrames`는 관측용 훅을 켜는 설정입니다. `observeFrames`와 `correctFramePose`가 모두 false이면 프레임 훅을 추가하지 않습니다.

게임에 넘기는 비행 후의 머리 자세와 표시 보정용 물리적 머리 자세는 다릅니다. 자세 갱신 훅만으로는 이 표시 경로의 정합성을 맞출 수 없었습니다. 항등 변환일 때는 원래 프레임 자세를 그대로 넘기고, 변환을 유지하는 동안에는 원래의 프레임 예측을 유지한 채 보정합니다. 이 수정 이후 알려진 흔들림이 해소되었다는 실제 기기 보고가 있습니다.

`timeBasedFramePose=false`는 예전의 이력 대조 방식으로 되돌립니다. 머리와 비행 자세가 동시에 움직이면 보정이 어긋나는 문제가 있던 방식입니다. 자세한 내용은 [드라이버 문서](native/driver/README.md)(일본어)를 참고해 주세요.

## 업데이트와 등록 해제

업데이트할 때는 FlugelKranz와 SteamVR을 종료해 DLL이 사용되지 않는 상태에서 빌드·배치합니다. 배포 폴더 전체와 보정 설정을 확인한 뒤 SteamVR, 게임, FlugelKranz를 실행해 주세요. 배치 폴더를 옮겼다면 예전 위치의 등록을 해제하고 새 위치를 등록합니다.

등록 해제는 배포 폴더에서 실행합니다.

```powershell
./install-driver.ps1 -Uninstall
```

해제한 뒤에는 SteamVR을 다시 시작합니다. 스크립트가 직접 다시 시작하지는 않습니다.

## 조작·설정·진단

앱은 OFF 상태로 시작합니다. `I`는 무한 보행, `F`는 자유 비행입니다. Touch에서는 그립 누르기가 Drag, 왼쪽 X / 오른쪽 A 누르기가 Turn입니다. OFF는 비행과 관성을 멈춥니다. F 모드에서 Drag가 켜져 있으면 OFF 중에도 잡아서 이동할 수 있습니다(놓을 때 관성 없음). Drag도 꺼져 있으면 현재 변환을 유지합니다. `↻`는 변환을 리셋합니다. 한 손·양손 조작과 관성 설정의 자세한 내용은 [README](README.ko.md#조작-사양)를 참고해 주세요.

```powershell
./FlugelKranz.exe --help
./FlugelKranz.exe --bindings
./FlugelKranz.exe --diagnose
```

`FlugelKranz.exe`는 GUI 앱(WinExe)이므로 인수 없이 실행해도 콘솔 창이 열리지 않습니다. 옵션을 붙이면 실행한 터미널에 출력합니다(터미널이 아닌 곳에서 실행하면 전용 콘솔을 열고, Enter로 닫습니다). PowerShell은 GUI 앱의 종료를 기다리지 않으므로, 결과와 종료 코드를 기다리려면 `./FlugelKranz.exe --diagnose | Out-Host`처럼 파이프로 연결해 주세요(그 뒤 `$LASTEXITCODE`로 종료 코드를 확인할 수 있습니다).

`--bindings`는 SteamVR의 바인딩 설정을 엽니다. `--diagnose`는 UI를 열지 않고 연결·물리 자세·논리 입력을 확인합니다. 진단할 때는 다른 FlugelKranz를 종료하고, HMD와 양손 컨트롤러를 추적할 수 있게 해 주세요. 입력이 모두 갖춰지지 않으면 0이 아닌 코드로 종료합니다.

설정 패널의 "Gravity compass"(「重力コンパス」)는 고른 손의 손등 위(기본은 오른손, 지름 약 4.5 cm. 왼쪽 손목의 손목시계형 오버레이와 겹치지 않게 하기 위해서이며, 설정에서 언제든 바꿀 수 있고 바로 반영됩니다)에 SteamVR 오버레이로 반투명 구(네브볼)를 띄웁니다. 앞쪽 반구만 그리며, 실제 수평선보다 위는 하늘색, 아래는 땅색으로 칠하고 그 경계에 밝은 수평선을 긋습니다. 실제 아래(중력)는 빛나는 점(구의 뒤쪽에 있을 때는 흐린 고리)으로 나타내고, 가장자리 바깥의 작은 빛나는 삼각형이 항상 실제 아래 방향을 가리킵니다. 작은 흰 점은 비행 중인 월드의 아래 방향이며(뒤쪽에서는 흐린 고리), 날기 전에는 빛나는 점과 겹칩니다("Show world down"(「ワールドの下を表示」)으로 전환). 3축 회전으로 월드가 기울어도 구는 실제 바닥의 방향을 보여 줍니다. 나침반은 새 설정에서는 릴리스 빌드에서 꺼짐, `-TestBuild` 빌드에서 켜짐입니다(저장된 값은 그대로). 표시하면 기본적으로 비행 ON인 동안에만 나타나며, 입력은 받지 않습니다. 이미지는 D3D11 텍스처를 `SetOverlayTexture`로 갱신하며, 설정란에 초당 이미지 수·그리기 시간·SteamVR 호출 시간을 표시합니다. 오버레이를 만들 수 없거나 SteamVR이 이미지를 거부하면 설정란에 이유를 표시하고, 마지막으로 받아들여진 이미지를 유지한 채 다시 시도하며, 비행은 그대로 계속합니다.

설정 저장 위치는 `%USERPROFILE%/.config/FlugelKranz/config.json`입니다. `FLUGELKRANZ_CONFIG`로 파일을, `XDG_CONFIG_HOME`으로 디렉터리를 지정할 수 있습니다. 기존에 저장된 값은 앱의 기본값과 별도로 복원됩니다.

## 동작 확인

2026-09-13, Quest 2 + Touch / Virtual Desktop / SteamVR에서 `5b41cab`의 시간 기반 보정을 켜고 확인했습니다.

- HMD·양손 Touch의 물리 자세 취득과 SteamVR 쪽으로의 비행 변환 반영을 확인.
- 회전을 유지하는 동안의 머리 움직임과 회전 입력 중의 머리 움직임에서, 예전의 검은 테두리·화면 어두워짐·지터가 개선되었다는 보고가 있음.
- 그 뒤에 보고된 시야 흔들림에 대해 `9932816`에서 항등 변환일 때의 통과 처리를, `c743e66`에서 변환을 유지하는 동안 원래의 프레임 예측을 유지하는 수정을 적용. 2026-09-14에 사용자로부터 흔들림이 해소되었다는 확인을 받았습니다.

SteamVR MirrorView가 정상으로 보여도 HMD 안의 화면과는 일치하지 않는 경우가 있었습니다. 검증할 때는 HMD 안에서 보이는 모습도 확인해 주세요. 게임별 시험, 전체 축의 체계적인 시험, 다른 HMD, FBT, 다른 자세 훅 도구와의 공존, 장시간 사용은 검증하지 않았습니다.

현재의 관성과 이동 속도에 대해서도 조정 요청이 있습니다. OVRAS의 관성 브레이크 10%·드래그 배율 1.4배에 가까운 조작감으로의 변경은 하지 않았으며, README의 기본값은 현재 구현의 값입니다.

## 문제 해결

| 증상 | 확인할 내용 |
| --- | --- |
| SteamVR에 연결할 수 없음 | SteamVR 실행 여부, 드라이버 등록, 애드온 활성화 상태, 다른 클라이언트의 연결 |
| 양손을 가져올 수 없음 | HMD를 착용하고 양쪽 컨트롤러가 SteamVR에서 추적되고 있는지 |
| 입력이 반응하지 않음 | FlugelKranz의 SteamVR 바인딩, ON 상태, 버튼을 뗐다가 다시 눌렀는지 |
| 회전할 때 검은 테두리·화면 어두워짐·흔들림 | 보정 설정과 로그, 회전 유지 중인지 갱신 중인지, 머리 움직임 여부, HMD 안과 MirrorView의 차이 |
| 기준 공간이 바뀌어 멈춤 | 연결 후 Standing 원점 변경이나 다른 공간 조작 도구의 동작. 다시 연결하기 전에 상태를 확인 |

SteamVR의 `logs/vrserver.txt`에 훅과 `FrameAudit` 로그가 나옵니다. 기본 설치 위치에서는 `C:/Program Files (x86)/Steam/logs/vrserver.txt`입니다. `history-matched pose correction installed`는 보정 훅이 설치되었다는 뜻이고, `timed=1`은 그 레이어에서 시간 기반 보정을 시도했다는 뜻입니다. 변환을 유지하는 동안에는 일정한 역변환만 적용하므로 `timed=0`·`corrected=1`이 되며, `held` 건수가 늘어납니다. `XYZ body-pose transforms v2`라는 시작 로그만으로는 보정이 켜졌는지 판단할 수 없습니다.

## 개발과 자동 테스트

```powershell
dotnet restore FlugelKranz.slnx
dotnet build FlugelKranz.slnx
dotnet test FlugelKranz.slnx
cmake -S native/driver -B artifacts/driver-build -A x64
cmake --build artifacts/driver-build --config Release
ctest --test-dir artifacts/driver-build -C Release --output-on-failure
```

C# 테스트는 입력, 좌표 합성, 관성, 모드 전환, 추적 상실, 복원, 설정, UI를 검증합니다. 네이티브 테스트는 자세·속도 변환, 항등 변환일 때의 통과 처리, 레이어 정보 유지, 프레임 자세의 보간·예측을 검증합니다. 자동 테스트 통과와 실제 기기에서의 표시 품질은 구별해 주세요.

Windows UI는 `UseWin32()`, 입력과 연결은 `FlugelKranz.OpenVR`, 조작 계산은 `FlugelKranz.Core`, 자세 적용과 프레임 보정은 `native/driver`가 맡습니다. 상위 저장소에서 이어받은 OpenXR / Monado 프로젝트는 의존성·참고용으로 남아 있지만, 여기서 Linux 설치·개발을 다루지는 않습니다.

## 머리 조종 시험 기능 실행

머리 조종과 독립된 Drag는 main에 통합되어 있습니다. 앱만 별도 폴더로 발행해 이미 등록된 드라이버에 연결하려면 다음 명령을 씁니다. 이 명령은 드라이버 DLL을 갱신하지 않으므로, 렌더링 보정 수정도 필요하다면 위의 빌드·드라이버 설치 절차를 써 주세요.

```powershell
dotnet publish src/FlugelKranz -c Release -r win-x64 --self-contained true -o artifacts/head-pilot-windows-x64
```

일반 버전의 FlugelKranz를 종료한 뒤 이 폴더의 앱을 실행합니다. 같은 앱 키를 쓰므로, SteamVR의 액션 manifest 등록은 마지막에 실행한 버전의 경로가 됩니다. 일반 버전으로 돌아가려면 일반 버전을 다시 실행해 주세요.

SteamVR 개발자 설정의 `Experimental overlay input overrides`를 켜고, 앱의 F 모드 설정에서 머리 조종을 켭니다. 프로그램은 이 전역 설정을 자동으로 바꾸지 않습니다. 입력이 대기 상태에 머물러 있으면 `/actions/pilot`의 왼쪽 스틱 추진과 X / A 전환 할당을 확인해 주세요. 조작 사양과 확인되지 않은 사항은 [README](README.ko.md#머리-조종-시험-기능windows--f-모드)를 참고해 주세요.

### VRChat 실행 중 입력 진단

SteamVR 대기 공간에서는 이동할 수 있지만 VRChat 안에서는 움직이지 않는다는 보고가 있어, 원인을 조사하고 있습니다. 설정 화면의 입력 진단에는 `requested`, `priority`, `dashboard`, `suspended`, `neutral`, `stickActive`, `y`, X/A의 활성·누름 상태, `scenePid`를 표시합니다. SteamVR 대기 공간과 VRChat 안에서 비교하고, 보내고 있는 오프셋도 함께 확인해 주세요. 진단 표시는 자세 변환이나 입력 우선순위를 바꾸지 않습니다.
