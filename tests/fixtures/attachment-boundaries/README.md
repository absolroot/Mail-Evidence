# 첨부 시그니처 검증용 메일

이 폴더의 EML은 검증용으로 합성했습니다. 세 메일을 함께 열면 **전체**에는 3통, **이상 징후**에는 2통이 표시됩니다.

| 메일 | 대조할 내용 |
|---|---|
| [01-normal.eml](01-normal.eml) | 정상 PNG·JPEG·GIF·PDF·ZIP과 BIN을 담았습니다. JPEG 세그먼트 안의 `FF D9`, ZIP 주석, PDF 끝 공백도 허용됩니다. |
| [02-mixed.eml](02-mixed.eml) | PNG로 이름을 바꾼 GIF의 시작 `0x0`, PNG 청크 길이 `0x21`, `IEND`가 없는 PNG의 파일 끝 `0x46`, JPEG 종료 뒤 `0x2B1`부터 추가된 4,620바이트를 확인할 수 있습니다. 정상 PNG도 들어 있습니다. |
| [03-container-boundaries.eml](03-container-boundaries.eml) | PDF 종료와 ZIP 중앙 디렉터리 종료 레코드 뒤에 붙은 검증용 텍스트를 각각 표시합니다. 주석이 있는 정상 ZIP도 들어 있습니다. |

**이상 징후**에서 항목을 선택하고 **첨부 HEX 보기**를 누르면 해당 위치가 열립니다.
