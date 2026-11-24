# 디스크 이미지에서 메일 찾기

Mail Evidence의 시작 화면과 설정에서 **디스크 이미지…**를 누르거나 이미지를 끌어 놓습니다. 분석할 NTFS 볼륨을 고른 뒤 메일 파일을 확인하고, 필요한 파일을 체크해 **선택한 파일 추출…**을 누릅니다. 저장할 폴더를 고른 다음 자료 선택 화면에서 **분석 시작**을 누릅니다.

- 첫 검색은 이미지 안의 사용자별 Outlook 폴더, 바탕 화면, 다운로드 폴더에서 수행합니다. 임의의 위치에 보관한 파일은 **볼륨 전체 검색**에서 찾을 수 있습니다. 확장자가 EML, MSG, MBOX, PST, OST인 현재 파일을 찾아 목록에 올립니다. 숨김·시스템 속성이 있는 파일도 포함하며 같은 NTFS 파일을 가리키는 하드 링크는 후보 한 개로 표시합니다.
- 전체 검색은 파일 목록인 `$MFT`를 한 번 순회합니다. 메일 확장자에 적중한 파일만 상위 폴더 참조를 따라 경로를 구하고, 실제 디렉터리의 파일 참조와 대조합니다. 현재 DiscUtils의 순회 인덱스에는 유효하지 않은 슬롯을 건너뛰는 경우가 있으므로 실제 경로의 파일 참조를 사용합니다. [Velociraptor의 파일명 필터](https://github.com/Velocidex/velociraptor/blob/master/artifacts/definitions/Windows/NTFS/MFT.yaml#L900-L931) · [DiscUtils 1.0.91 순회 코드](https://github.com/LTRData/DiscUtils/blob/6f988ba4b569a31b0611543b98a8e7fff747c8c7/Library/DiscUtils.Ntfs/MasterFileTable.cs#L127-L153)
- 추출과 이미지 SHA-256은 후보 검색 이후 별도로 진행합니다. 결과 폴더의 `manifest.json`에는 이미지 구성 파일과 추출 파일의 해시, NTFS 파일 참조가 기록됩니다. 분석 시 추출 파일의 SHA-256을 매니페스트 작성 당시의 값과 대조하며, **메일 정보 → 자료 출처**에서 이미지 경로, 볼륨 시작 위치, 이미지 내부 경로와 매니페스트를 확인할 수 있습니다. 메일 내보내기 기록에도 세션의 이미지 출처가 함께 기록됩니다.

물리 이미지의 읽을 수 있는 NTFS 볼륨을 대상으로 합니다. RAW·물리 EWF·VHD/VHDX 등은 [NTFS Tracer 이미지 모듈의 포맷 및 변형 지원 범위](../third_party/ntfs-tracer/modules/EvidenceImage/README.md)를 따릅니다. 잠긴 BitLocker, 논리 EWF, 삭제 파일 복구, 새 Outlook 전용 캐시 판독은 현재 메일 이미지 검색 범위 밖입니다. 2 GiB를 넘는 PST·OST는 후보에서 확인할 수 있고 추출 선택은 비활성화됩니다. EWF를 읽을 때 배포 폴더의 `NtfsTracer.libewf.dll`과 실행 파일을 같은 폴더에 둡니다.

## 속도와 원본 대조

동일한 볼륨에서 숨김·시스템 파일을 양쪽에 포함했습니다. Windows에서 두 방식을 번갈아 실행하고 첫 실행을 제외한 세 번의 중앙값을 기록했습니다. 시간은 볼륨을 선택한 뒤 후보를 모으는 구간입니다. 이미지 SHA-256, 메일 본문 분석과 화면 그리기는 별도입니다.

| 표본 | 후보 | MFT 순회 | 디렉터리 탐색 |
|---|---:|---:|---:|
| 합성 32 MiB RAW | 2 | 1.63 ms | 1.11 ms |
| 위와 같은 논리 내용의 압축 E01 | 2 | 3.45 ms | 2.40 ms |
| 기존 검증용 22 GiB RAW | 304 | 1,791.51 ms | 1,887.02 ms |

22 GiB에서 첫 후보가 검색 엔진에서 전달되기까지 중앙값은 5.93 ms였습니다. The Sleuth Kit 4.15.0 `fls`와 세 입력의 후보 경로 및 MFT 레코드 번호를 대조했고 모두 일치했습니다. 합성 RAW와 E01에서는 `icat`으로 읽은 EML·PST의 SHA-256을 원본 표본과 각각 대조했습니다. 실제 WPF에서 후보 찾기, 선택 추출, 분석, 자료 출처 확인 및 원본 이미지의 SHA-256 보존을 검증했습니다. 대용량 E01과 물리 HDD의 콜드 캐시 성능은 미측정입니다. [The Sleuth Kit 배포본](https://github.com/sleuthkit/sleuthkit/releases/tag/sleuthkit-4.15.0)
