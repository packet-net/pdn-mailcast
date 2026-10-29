| key | origin | LAA/LAF origin copy | next hop(s) after stripping R:/From/To | notes |
| t01_plain | LAA | 44B title 13 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t01_plain | LAF | 44B title 13 | lab: same (+121B prefix) | |
| t02_from_line | LAA | 99B title 17 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t02_from_line | LAF | 99B title 17 | lab: same (+121B prefix) | |
| t03_from_to_top | LAA | 78B title 19 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t03_from_to_top | LAF | 78B title 19 | lab: same (+121B prefix) | |
| t04_trailing_ws | LAA | 94B title 19 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t04_trailing_ws | LAF | 94B title 19 | lab: same (+121B prefix) | |
| t05_blank_first | LAA | 66B title 19 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t05_blank_first | LAF | 66B title 19 | lab: same (+121B prefix) | |
| t06_long_lines | LAA | 7056B title 18 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t06_long_lines | LAF | 7057B title 18 | lab: same (+121B prefix) | |
| t07_title40 | LAA | 28B title 40 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t07_title40 | LAF | 28B title 40 | lab: same (+121B prefix) | |
| t08_title60 | LAA | 28B title 59 | laf: same (+55B prefix); lab: same (+122B prefix) | |
| t08_title60 | LAF | 28B title 60 | lab: same (+121B prefix) | |
| t09_title100 | LAA | 21B title 59 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t09_title100 | LAF | 21B title 60 | lab: same (+121B prefix) | |
| t10_latin1 | LAA | 79B title 12 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t10_latin1 | LAF | 79B title 12 | lab: same (+121B prefix) | |
| t11_utf8 | LAA | 55B title 21 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t11_utf8 | LAF | 55B title 21 | lab: same (+121B prefix) | |
| t12_no_final_newline | LAA | 49B title 24 | laf: differs: 50 vs 49 bytes (+56B prefix); lab: differs: 50 vs 49 bytes (+124B prefix) | |
| t12_no_final_newline | LAF | 47B title 24 | lab: same (+121B prefix) | |
| t13_ends_blank_lines | LAA | 12B title 24 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t13_ends_blank_lines | LAF | 12B title 24 | lab: same (+121B prefix) | |
| t14_crlf_body | LAA | 32B title 17 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t14_crlf_body | LAF | 32B title 17 | lab: same (+121B prefix) | |
| t16_dot_line | LAA | 30B title 16 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t16_dot_line | LAF | 30B title 16 | lab: same (+121B prefix) | |
| t17_r_lines | LAA | 176B title 15 | laf: differs: 75 vs 176 bytes (+155B prefix); lab: differs: 75 vs 176 bytes (+223B prefix) | |
| t17_r_lines | LAF | 176B title 15 | lab: differs: 75 vs 176 bytes (+159B prefix) | |
| t18_very_long | LAA | 53601B title 17 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t18_very_long | LAF | 51400B title 17 | lab: same (+121B prefix) | |
| t19_gt_from_and_misc | LAA | 72B title 24 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t19_gt_from_and_misc | LAF | not created | | |
| t20_ctrl_chars | LAA | 37B title 18 | laf: same (+56B prefix); lab: same (+124B prefix) | |
| t20_ctrl_chars | LAF | 37B title 18 | lab: same (+121B prefix) | |
