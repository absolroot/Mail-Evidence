# 첨부 구조 샘플

합성 EML 세 통을 함께 열면 **전체**는 3통, **이상 징후**는 2통입니다.

| 메일 | 대조할 내용 |
| --- | --- |
| [01-normal.eml](01-normal.eml) | 정상 PNG·JPEG·GIF·PDF·ZIP과 BIN. JPEG 세그먼트 안의 `FF D9`, ZIP 주석, PDF 끝 공백은 정상 구조입니다. |
| [02-mixed.eml](02-mixed.eml) | GIF 시작 바이트 `0x0`, 잘못된 PNG 청크 길이 `0x21`, `IEND`가 없는 PNG의 파일 끝 `0x46`, JPEG 종료 뒤 `0x2B1`부터 이어지는 추가 4,620바이트. |
| [03-container-boundaries.eml](03-container-boundaries.eml) | PDF와 ZIP 종료 뒤에 붙은 추가 바이트. 주석이 있는 정상 ZIP도 포함합니다. |

**이상 징후**에서 항목을 고르고 **첨부 HEX 자세히 보기**를 누르면 디코딩한 첨부의 해당 위치가 열립니다.
