# 첨부파일 시그니처와 종료 구조

Windows 앱에서 메일을 열면 첨부파일 이름으로 예상한 형식과 디코딩한 바이트를 대조합니다. 시작 시그니처 불일치, 내부 구조 불일치, 종료 구조 미확인, 종료 뒤 추가 바이트를 **이상 징후**에 표시합니다. 목록 위 필터의 **첨부파일 시작·끝 구조 이상**으로 해당 메일을 좁힐 수 있습니다.

## 검사하는 바이트

| 파일 형식 | 시작 구조 | 종료 구조 또는 경계 |
|---|---|---|
| PNG | `89 50 4E 47 0D 0A 1A 0A` | 청크를 순서대로 읽어 길이 0의 `IEND`와 CRC를 확인합니다. 전체 종료 청크는 `00 00 00 00 49 45 4E 44 AE 42 60 82`입니다. |
| JPEG, JFIF | `FF D8 FF` | 마커와 세그먼트 길이를 읽어 `FF D9`를 찾습니다. 세그먼트 안에 있는 같은 바이트는 건너뜁니다. MPF가 있으면 첫 이미지 뒤의 바이트를 추가 데이터로 표시하지 않습니다. |
| GIF | `GIF87a`, `GIF89a` | 블록과 하위 블록을 따라 `3B` 종료자를 찾습니다. |
| PDF | 처음 1,024바이트 안의 `%PDF-n.n` | 끝 128 KiB에서 `startxref`, 숫자, `%%EOF` 순서를 찾습니다. 뒤에 붙은 PDF 공백을 허용합니다. |
| ZIP, DOCX·XLSX·PPTX, DOCM·XLSM·PPTM, HWPX, ODT·ODS·ODP, EPUB | `50 4B 03 04` 또는 빈 ZIP의 `50 4B 05 06` | 중앙 디렉터리 종료 레코드 `50 4B 05 06`을 찾습니다. 레코드에 기록된 ZIP 주석 길이도 반영합니다. |
| WebP, WAVE | 오프셋 0의 `RIFF`와 오프셋 8의 `WEBP` 또는 `WAVE` | 오프셋 4의 4바이트 길이로 컨테이너 경계를 확인합니다. `RF64`는 시작 구조만 확인합니다. |
| HWP | `D0 CF 11 E0 A1 B1 1A E1` 또는 구형 `HWP Document File V` | 시작 구조만 확인합니다. |
| TIFF, BigTIFF | `49 49 2A 00`, `4D 4D 00 2A`, `49 49 2B 00`, `4D 4D 00 2B` | 시작 구조만 확인합니다. |
| BMP, 7z, GZIP, RAR | BMP `42 4D`. 7z `37 7A BC AF 27 1C`. GZIP `1F 8B 08`. RAR `52 61 72 21 1A 07` 뒤에 `00` 또는 `01 00`. | 시작 구조만 확인합니다. |

[SEARCH 시그니처 목록](https://filesig.search.org/)에서 검사할 형식을 골랐습니다. 종료 규칙은 [PNG](https://www.w3.org/TR/png/), [JPEG](https://www.itu.int/itu-t/recommendations/rec.aspx?id=2633&lang=en), [GIF](https://www.w3.org/Graphics/GIF/spec-gif89a.txt), [PDF](https://pdf-issues.pdfa.org/32000-2-2020/clause07.html), [ZIP](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT), [WebP](https://developers.google.com/speed/webp/docs/riff_container) 명세를 참고했습니다.

DOCX와 HWPX는 공통 ZIP 구조를 검사합니다. 표에 없는 확장자는 검사 대상에서 제외합니다. 첨부가 8 MiB를 넘거나 바이트를 읽을 수 없으면 확인 불가로 표시합니다.

## HEX에서 근거 확인

**이상 징후**에서 판단에 사용한 위치와 바이트를 미리 볼 수 있습니다. **첨부 HEX 자세히 보기**를 누르면 문제가 시작되는 위치로 이동합니다. 제목과 설명에 실제 오프셋을 표시하고, 해당 바이트의 HEX 값과 ASCII 값을 붉은 테두리로 강조합니다. 종료 구조가 없으면 파일 끝 오프셋과 누락한 표식을 표시합니다. 표시한 오프셋의 기준은 디코딩한 첨부이며, **원본 위치**를 펼치면 인코딩한 MIME 범위, MSG 스트림 또는 PST·OST 속성과 첨부 SHA-256을 확인할 수 있습니다. 범위를 설명하고 해당 위치로 이동하는 방식은 [ImHex의 바이트 범위 북마크](https://docs.werwolv.net/imhex/views/bookmarks)를 참고했습니다.

[검증용 메일 3통](../tests/fixtures/attachment-boundaries/README.md)으로 시작 불일치, 길이 필드 오류, 종료 표식 누락, 종료 뒤 추가 바이트와 정상 변형을 함께 확인할 수 있습니다.
