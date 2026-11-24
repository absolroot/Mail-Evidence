# 디스크 이미지에서 메일 열기

1. **디스크 이미지…**에서 파일을 열고 읽을 수 있는 NTFS 볼륨을 고릅니다.
2. Outlook 폴더, 바탕 화면, 다운로드 폴더에서 후보를 찾습니다. 다른 위치에 저장한 파일은 **볼륨 전체 검색**을 사용하세요.
3. 필요한 파일을 체크하고 **선택한 파일 추출…**을 누릅니다. 출력 폴더를 지정한 뒤 **분석 시작**을 누릅니다.

EML·MSG·MBOX·PST·OST를 찾습니다. 숨김·시스템 파일도 검색하며, 같은 NTFS 파일을 가리키는 하드 링크는 후보 하나로 셉니다. 2 GiB를 넘는 PST·OST는 목록에 표시되고 추출은 비활성화됩니다.

RAW·E01·Ex01·가상 디스크의 세부 조건은 이미지 모듈 문서에서 확인할 수 있습니다. E01·Ex01을 열 때는 `NtfsTracer.libewf.dll`을 실행 파일과 같은 폴더에 둡니다. 잠긴 BitLocker, 논리 EWF, 삭제 파일 복구, 새 Outlook 전용 캐시는 이 검색 범위 밖입니다. [이미지 형식과 제한](../third_party/ntfs-tracer/modules/EvidenceImage/README.md)

## 추출 결과

출력 폴더의 `manifest.json`에 이미지 구성 파일과 추출 파일의 SHA-256, NTFS 파일 참조가 남습니다. 분석을 시작할 때 추출 파일의 해시를 매니페스트와 대조합니다. **메일 정보 → 자료 출처**에서 이미지 경로, 볼륨 시작 위치와 이미지 내부의 파일 경로를 확인할 수 있습니다.

합성 RAW·E01과 검증용 22 GiB RAW에서 후보 경로와 MFT 레코드 번호를 The Sleuth Kit 4.15.0의 `fls` 결과와 대조했습니다. 큰 RAW의 후보 304건이 모두 일치했습니다. 합성 이미지의 EML·PST 추출 파일은 `icat`으로 읽은 바이트와 SHA-256이 같았습니다. [디스크 이미지 화면 시험](../tools/FlowSmoke/Program.DiskImage.cs) · [The Sleuth Kit 4.15.0](https://github.com/sleuthkit/sleuthkit/releases/tag/sleuthkit-4.15.0)
