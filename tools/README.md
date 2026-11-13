# Nástroje pre web Reguly

Vyrábajú obrázky a video do `~/regula/web/` priamo z appky (offscreen, bez otvárania okien,
simuluje ThinkPad 1920×1080 / 309 mm) — v ANGLICKEJ verzii (dočasný config bez "lang").
Spúšťať cez venv s PySide6:

    ~/.venvs/pyside6/bin/python web_shots.py    # screenshoty img/*.webp + ikonky img/icons/
    ~/.venvs/pyside6/bin/python calib_shot.py   # img/calibrate.webp
    ~/.venvs/pyside6/bin/python web_video.py    # media/demo.mp4 + demo.webm (treba ffmpeg)

Po zmene appky obnov aj súbory na stiahnutie:

    cp ~/regula/regula.py ~/regula/web/download/regula.py
    (a znova zbaľ web/download/regula.zip = regula/regula.py + regula/regula.svg + regula/LICENSE)

Jazyk appky: predvolene angličtina (tá je na webe); slovensky cez "lang": "sk" v ~/.config/regula/config.json
alebo REGULA_LANG=sk. Každý text je v kóde ako L("slovensky", "english").
