"""Generate editable social visuals and pixel exports without application data."""

import argparse
import base64
import json
from pathlib import Path
import struct
import subprocess
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets"
NAVY = "#091220"
PANEL = "#111F32"
LINE = "#29405C"
WHITE = "#F4F8FF"
MUTED = "#A9BCD3"
BLUE = "#65B2FF"
MINT = "#62E6C6"
MARK_BLUE = "#1A5FB4"
SPARK = "#FFC83D"
FONT = "Segoe UI, Arial, sans-serif"


def rect(x, y, w, h, fill=PANEL, radius=20, stroke=None):
    return f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{radius}" fill="{fill}"' + (f' stroke="{stroke}" stroke-width="2"' if stroke else "") + "/>"


def text(x, y, value, size=26, fill=WHITE, weight=400, anchor="start", spacing=0):
    return f'<text x="{x}" y="{y}" font-family="{FONT}" font-size="{size}" font-weight="{weight}" fill="{fill}" text-anchor="{anchor}" letter-spacing="{spacing}">{escape(value)}</text>'


def lines(x, y, values, size=26, gap=None, **kwargs):
    return "".join(text(x, y + i * (gap or size * 1.32), value, size, **kwargs) for i, value in enumerate(values))


def line(x1, y1, x2, y2, color=LINE, width=2, arrow=False):
    return f'<path d="M{x1} {y1} L{x2} {y2}" stroke="{color}" stroke-width="{width}" fill="none"' + (' marker-end="url(#arrow)"' if arrow else "") + "/>"


def dot(x, y, r=5, fill=MINT):
    return f'<circle cx="{x}" cy="{y}" r="{r}" fill="{fill}"/>'


def mark(x, y, size=48):
    """Envelope with a spark on a blue tile, drawn on a 64x64 grid."""
    return (f'<g transform="translate({x} {y}) scale({size / 64:g})">' + rect(0, 0, 64, 64, MARK_BLUE, 14)
            + rect(11, 21, 36, 25, "#FFFFFF", 3.5)
            + f'<path d="M12 23 L29 35 L46 23" fill="none" stroke="{MARK_BLUE}" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>'
            + f'<path d="M48 7 Q49.6 16.4 59 18 Q49.6 19.6 48 29 Q46.4 19.6 37 18 Q46.4 16.4 48 7Z" fill="{SPARK}" stroke="{MARK_BLUE}" stroke-width="2.5" stroke-linejoin="round" paint-order="stroke"/></g>')


def brand(x=72, y=64):
    return mark(x, y, 46) + text(x + 64, y + 33, "OnlyWorkspace", 30, weight=600)


def svg(w, h, content, title, desc, background=True):
    defs = '<defs><linearGradient id="bg" x2="1" y2="1"><stop stop-color="#07101E"/><stop offset="1" stop-color="#142D49"/></linearGradient><radialGradient id="glow"><stop stop-color="#186EAE" stop-opacity=".34"/><stop offset="1" stop-color="#186EAE" stop-opacity="0"/></radialGradient><marker id="arrow" markerWidth="9" markerHeight="9" refX="8" refY="4" orient="auto"><path d="M0 0 L8 4 L0 8" fill="none" stroke="#65B2FF" stroke-width="1.5"/></marker></defs>'
    bg = rect(0, 0, w, h, "url(#bg)", 0) + f'<ellipse cx="{w * .85}" cy="{h * .2}" rx="{w * .6}" ry="{h * .65}" fill="url(#glow)"/>' if background else ""
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" role="img" aria-labelledby="title desc"><title id="title">{escape(title)}</title><desc id="desc">{escape(desc)}</desc>{defs}{bg}{content}</svg>\n'


def save(path, w, h, content, title, desc, background=True):
    target = OUT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(svg(w, h, content, title, desc, background), encoding="utf-8", newline="\n")


def chip(x, y, label, width=None, fill="#15324A", color=BLUE):
    width = width or len(label) * 12 + 34
    return rect(x, y, width, 42, fill, 21) + text(x + 17, y + 28, label, 21, color, 600)


def mail_icon(x, y, size=44, color=BLUE):
    return rect(x, y, size, size * .72, "none", 6, color) + f'<path d="M{x + 2} {y + 3} L{x + size / 2} {y + size * .39} L{x + size - 2} {y + 3}" fill="none" stroke="{color}" stroke-width="3"/>'


def panel(index, x, y, w, h):
    """Keep a common 920x470 coordinate space for format-specific layouts."""
    s = rect(0, 0, 920, 470, "#0D1A2B", 28, LINE)
    if index == 1:
        s += text(32, 50, "DALLA POSTA AL CONTESTO", 19, MUTED, 600, spacing=2)
        s += rect(30, 82, 330, 340, PANEL, 18)
        for i, label in enumerate(["Progetto Aurora", "Consegna aggiornata", "Prossimi passi"]):
            yy = 112 + i * 100
            s += mail_icon(52, yy + 8, 30) + text(100, yy + 22, label, 22, weight=600)
            s += text(100, yy + 53, "Email di esempio", 18, MUTED)
            if i < 2:
                s += line(52, yy + 78, 335, yy + 78)
        s += line(390, 242, 445, 242, BLUE, 3, True)
        s += rect(482, 82, 408, 340, "#16314A", 18)
        s += chip(507, 108, "Sintesi", 110) + text(508, 194, "Le informazioni utili.", 28, weight=600)
        for i, label in enumerate(["Decisioni concordate", "Punti ancora aperti", "Riferimenti alle email"]):
            s += dot(518, 240 + i * 58) + text(540, 247 + i * 58, label, 23)
    elif index == 2:
        s += text(32, 50, "QUESTA EMAIL", 19, MUTED, 600, spacing=2)
        s += rect(30, 82, 390, 340, PANEL, 18)
        s += mail_icon(58, 112, 38) + text(118, 139, "Progetto Aurora", 27, weight=600)
        s += lines(58, 207, ["Consegna: venerdì.", "Confermare il referente.", "Rivedere il documento."], 25, gap=55)
        s += chip(56, 350, "Testo sintetico", 198)
        s += line(442, 242, 482, 242, BLUE, 3, True)
        s += rect(520, 82, 370, 340, "#16314A", 18)
        s += text(549, 139, "Sintesi e azioni", 28, weight=600)
        for i, label in enumerate(["Comprendi il messaggio", "Individua le attività", "Prepara una risposta"]):
            s += text(549, 210 + i * 61, f"0{i + 1}", 21, MINT, 600) + text(594, 210 + i * 61, label, 20)
    elif index == 3:
        s += text(32, 50, "RICOSTRUISCI FLUSSO", 19, MUTED, 600, spacing=2)
        s += line(72, 133, 72, 350, BLUE, 3)
        for i, (label, status) in enumerate([("La richiesta iniziale", "[M1]  ·  Email originale"), ("La decisione condivisa", "[M2]  ·  Risposta"), ("Il prossimo passo", "[M3]  ·  Conferma")]):
            yy = 92 + i * 112
            s += dot(72, yy + 40, 10, MINT if i == 2 else BLUE)
            s += rect(114, yy, 764, 92, PANEL, 16)
            s += text(140, yy + 37, label, 27, weight=600) + text(140, yy + 69, status, 20, MUTED)
        s += text(116, 450, "Esempio di cronologia con riferimenti alle fonti", 19, MUTED)
    elif index == 4:
        for xx, label, sub, color in [(30, "Ollama locale", "Modello sul tuo PC", MINT), (482, "Provider cloud", "Profilo configurato", BLUE)]:
            s += rect(xx, 82, 408, 230, PANEL, 18)
            s += dot(xx + 35, 125, 7, color) + text(xx + 57, 134, label, 29, weight=600)
            s += text(xx + 27, 191, sub, 23, MUTED)
            s += line(xx + 28, 222, xx + 375, 222)
            s += text(xx + 27, 265, "Scelta del profilo IA", 22, color)
        s += text(32, 50, "UN PROFILO ESPLICITO", 19, MUTED, 600, spacing=2)
        s += rect(30, 340, 860, 82, "#16314A", 16)
        s += text(58, 375, "Il riquadro indica sempre chi riceve i dati.", 25, weight=600)
        s += text(58, 406, "Informativa per i servizi esterni; livello di invio per profilo.", 19, MUTED)
    elif index == 5:
        s += text(32, 50, "SCRIVI  /  MIGLIORA  /  TRADUCI  /  OGGETTO", 19, MUTED, 600, spacing=2)
        s += rect(30, 82, 860, 250, PANEL, 18)
        s += text(60, 130, "Re: Progetto Aurora", 28, weight=600)
        s += lines(60, 188, ["Grazie per l'aggiornamento.", "Confermo la presa in carico delle attività.", "Verifico i dettagli prima dell'invio."], 25, gap=43)
        s += chip(30, 366, "01  Leggi", 230) + chip(290, 366, "02  Modifica", 260)
        s += chip(580, 366, "03  Decidi tu", 310, "#153C38", MINT)
    elif index == 7:
        s += text(32, 50, "VERIFICA SICUREZZA", 19, MUTED, 600, spacing=2)
        s += rect(30, 82, 450, 340, PANEL, 18)
        s += text(58, 130, "Segnali locali, subito", 26, weight=600)
        for i, label in enumerate(["SPF, DKIM, DMARC dichiarati", "Reply-To e domini simili", "Link e allegati", "Microsoft Defender sulle copie"]):
            s += dot(66, 186 + i * 58) + text(88, 193 + i * 58, label, 22)
        s += line(500, 242, 540, 242, BLUE, 3, True)
        s += rect(560, 82, 330, 340, "#16314A", 18)
        s += chip(586, 108, "Spiegazione IA", 196)
        s += lines(588, 215, ["Cita i dati ricevuti.", "Non abbassa il", "rischio tecnico."], 25, gap=46, weight=600)
        s += text(588, 390, "Se consentita dal profilo", 19, MUTED)
    elif index == 8:
        s += text(32, 50, "WEB SEARCH PER PROFILO", 19, MUTED, 600, spacing=2)
        s += rect(30, 82, 860, 78, "#16314A", 16)
        s += dot(62, 121, 7, BLUE) + text(84, 129, "Profilo IA · cloud · ricerca web", 24, BLUE, 600)
        s += rect(30, 186, 420, 236, PANEL, 18)
        s += text(58, 236, "Azioni scelte", 26, weight=600)
        s += lines(58, 288, ["L'amministratore abilita la", "ricerca per profilo e azione."], 21, gap=34, fill=MUTED)
        s += rect(470, 186, 420, 236, PANEL, 18)
        s += text(498, 236, "Fonti apribili", 26, weight=600)
        for i, label in enumerate(["[W1]  https://…", "[W2]  https://…"]):
            s += text(498, 290 + i * 40, label, 21, MINT)
        s += text(498, 392, "Solo fonti citate, anche nello storico.", 18, MUTED)
    elif index == 9:
        s += text(32, 50, "BRIEFING E FOLLOW-UP", 19, MUTED, 600, spacing=2)
        for xx, label, items, color in [(30, "Briefing", ["Agenda di oggi", "Triage dei non letti", "Categorie su conferma"], MINT), (482, "Follow-up", ["Precedenti nella conversazione", "Ricerca nell'archivio", "Punti ancora da seguire"], BLUE)]:
            s += rect(xx, 82, 408, 340, PANEL, 18)
            s += dot(xx + 35, 128, 7, color) + text(xx + 57, 137, label, 29, weight=600)
            s += line(xx + 28, 170, xx + 375, 170)
            for i, item in enumerate(items):
                s += text(xx + 27, 230 + i * 62, item, 22)
    else:
        s += text(32, 50, "IL FLUSSO, IN TRE PASSI", 19, MUTED, 600, spacing=2)
        for i, (label, detail) in enumerate([("Seleziona", "Email o ambito"), ("Analizza", "Un obiettivo chiaro"), ("Verifica", "Fonti e risultato")]):
            xx = 30 + i * 294
            s += rect(xx, 92, 272, 254, PANEL, 18)
            s += text(xx + 25, 157, f"0{i + 1}", 45, MINT, 600)
            s += text(xx + 25, 230, label, 31, weight=600) + text(xx + 25, 281, detail, 20, MUTED)
        s += text(460, 410, "Add-in per Outlook classico · Qualifica interna in corso.", 24, BLUE, anchor="middle")
    k = min(w / 920, h / 470)
    return f'<g transform="translate({x + (w - 920 * k) / 2} {y + (h - 470 * k) / 2}) scale({k})">{s}</g>'


def hero(w, h):
    content = brand(60, 45)
    content += text(60, 208, "La tua posta.", 66, weight=600) + text(60, 286, "Un contesto chiaro.", 66, weight=600)
    content += lines(64, 354, ["Sintesi, ricerca e bozze", "dentro Outlook classico."], 29, gap=43, fill=MUTED)
    content += chip(60, h - 135, "OUTLOOK CLASSICO", 252) + chip(327, h - 135, "WINDOWS", 166)
    content += rect(780, 154, w - 838, 325, "#0E1F32", 26, LINE)
    content += mail_icon(817, 195, 48) + text(817, 288, "Email", 27, weight=600)
    content += line(817, 318, w - 100, 318, BLUE, 2)
    for i, label in enumerate(["Contesto", "Punti aperti", "Bozze"]):
        content += dot(825, 356 + i * 43, 5) + text(845, 363 + i * 43, label, 23)
    content += text(60, h - 38, "ONLYAPPS  ·  QUALIFICA INTERNA IN CORSO", 17, MUTED, spacing=1)
    return content


def architecture():
    content = brand(70, 48) + text(70, 170, "Dal contesto Outlook al risultato.", 54, weight=600)
    content += text(70, 219, "Confini di esecuzione e dati · Architettura del repository", 25, MUTED)
    content += line(460, 360, 606, 360, BLUE, 3, True) + text(535, 333, "OOM · UI", 20, BLUE, anchor="middle")
    content += line(990, 360, 1136, 360, BLUE, 3, True) + text(1064, 333, "Richiesta IA", 20, BLUE, anchor="middle")
    content += line(800, 446, 800, 640, BLUE, 3, True) + text(822, 541, "Dati semplici", 22, BLUE)
    content += text(822, 575, "Pipe locale · SID", 20, MUTED)
    content += line(650, 446, 300, 640, BLUE, 3, True) + text(291, 550, "Allegati temporanei", 20, BLUE)
    content += line(990, 720, 1136, 720, BLUE, 3, True) + text(1064, 693, "Letture / scritture", 17, BLUE, anchor="middle")
    nodes = [(90, 280, "Outlook classico", ["Store aperti nel profilo", "Originali email disponibili"], "01"), (610, 280, "Add-in + WebView2", ["Riquadro e ribbon", "Accesso Outlook sul thread UI"], "02"), (1140, 280, "Client IA", ["Ollama locale o cloud", "Profilo e policy configurati"], "03"), (90, 640, "AttachmentExtractor", ["Processo separato", "Timeout e limite di memoria"], "04"), (610, 640, "ConfigTool · tray", ["ArchiveEngine", "Unico proprietario dei DB"], "05"), (1140, 640, "SQLite cifrato", ["Metadati, indice e testo acquisito", "Chiave DPAPI per utente"], "06")]
    for x, y, title, detail, num in nodes:
        content += rect(x, y, 370 if x == 90 else 380, 170, PANEL, 20, LINE)
        content += text(x + 25, y + 38, num, 19, MINT, 600)
        content += text(x + 25, y + 79, title, 27, weight=600)
        content += lines(x + 25, y + 116, detail, 20, gap=30, fill=MUTED)
    content += rect(90, 869, 1430, 76, "#142D44", 15)
    content += text(118, 915, "Core: modelli, prompt e regole senza COM  ·  Configurazione e vault separati dai dati utente", 24)
    content += text(90, 982, "Raccolta con Outlook aperto · Ricerca nell'indice anche offline · Invio cloud secondo policy e informativa", 22, MUTED)
    save("architecture/system.svg", 1600, 1040, content, "Architettura OnlyWorkspace", "Outlook classico espone gli store all'add-in sul thread UI. L'add-in chiama il client IA, estrae allegati in un processo separato e invia dati via pipe al motore nella tray, proprietario del database cifrato.")
    (OUT / "architecture/system.mmd").write_text('''flowchart LR
    O["Outlook classico<br/>Store aperti e originali"] -->|"OOM: thread UI"| A["Add-in COM<br/>Ribbon e WebView2"]
    A -->|"Richiesta secondo profilo e policy"| L["Client IA<br/>Ollama locale o provider cloud"]
    A -->|"Copie temporanee"| X["AttachmentExtractor<br/>Processo isolato"]
    A <-->|"Dati semplici: pipe locale con verifica SID"| T["ConfigTool / tray<br/>ArchiveEngine"]
    T <-->|"Unico proprietario"| D[("SQLite cifrato<br/>Metadati, indice e testo acquisito")]
    C["ConfigTool GUI / CLI"] --> F["config.json e vault DPAPI LocalMachine"]
    F -.-> A
    F -.-> T
    K["Core: modelli, prompt, regole senza COM"] -.-> A
    K -.-> T
    D --- P["Chiave archivio DPAPI CurrentUser"]
''', encoding="utf-8", newline="\n")


def embed(path):
    if not path.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError(f"Expected actual PNG bytes for embedded screenshot: {path}")
    return "data:image/png;base64," + base64.b64encode(path.read_bytes()).decode()


def image(x, y, w, h, path, iw, ih):
    """Fit a PNG inside a box, keeping its aspect ratio."""
    k = min(w / iw, h / ih)
    dw, dh = iw * k, ih * k
    return f'<image x="{x + (w - dw) / 2}" y="{y + (h - dh) / 2}" width="{dw}" height="{dh}" href="{embed(path)}"/>'


def bullets(x, y, items, size=24, gap=50, color=MINT):
    return "".join(dot(x + 6, y - size * .35 + i * gap, 6, color) + text(x + 26, y + i * gap, item, size) for i, item in enumerate(items))


# Deck slides: (kind, eyebrow, title, subtitle, body). Statements follow docs/utente/riquadro.md, docs/admin/sicurezza.md and docs/tecnico/archivio-motore.md.
DECK = [
    ("hero", None, None, None, None),
    ("text", "Il problema", "Il contesto è sparso.", "Una decisione in una risposta. Una scadenza nell'allegato.", (["Un punto aperto in una conversazione precedente.", "Una richiesta che attende risposta da giorni.", "Un mittente che sembra noto, ma non lo è."], ["OnlyWorkspace porta", "gli strumenti IA nel", "riquadro di Outlook", "classico, accanto", "all'email aperta."])),
    ("panel", "Questa email", "Dal messaggio ai punti chiave.", "Sintesi, Verifica sicurezza, Bozza risposta, Attività e appuntamenti, Estrai dati.", 2),
    ("text", "Cerca e analizza", "Ritrova il filo nella corrispondenza.", "Ambiti: conversazione, cartella, cassette aperte, cartelle scelte o tutto l'archivio.", (["Riepilogo e punti aperti", "Tabella dati e CSV", "Appuntamenti e scadenze", "Email senza risposta"], ["Filtri per persona, oggetto,", "testo e periodo.", "Risultati con riferimenti", "alle email lette."])),
    ("panel", "Ricostruisci flusso", "Richiesta, decisione, prossimo passo.", "Copie collegate da Message-ID, In-Reply-To e conversazione Outlook.", 3),
    ("panel", "Verifica sicurezza", "Controlla prima di rispondere.", "Segnali locali subito; la spiegazione IA non abbassa il rischio tecnico.", 7),
    ("panel", "Profili IA", "Scegli come usare l'IA.", "Ollama locale o profili OpenAI, Anthropic, Gemini e QwenCloud configurati.", 4),
    ("panel", "Web Search", "Fonti dal web, quando serve.", "Abilitata dall'amministratore per singolo profilo e per azione.", 8),
    ("panel", "Briefing e follow-up", "La giornata e i seguiti.", "Agenda di oggi, triage dei non letti, precedenti di una conversazione.", 9),
    ("shot", "Build installata", "Sintesi dell'email selezionata.", None, "screenshots/installed/01-sintesi.png"),
    ("shot", "Build installata", "Verifica sicurezza: segnali e giudizio.", None, "screenshots/installed/02-verifica-sicurezza.png"),
    ("shot", "Build installata", "Estrai dati: tabella copiabile e CSV.", None, "screenshots/installed/03-estrai-dati.png"),
    ("image", "Architettura", "Confini chiari tra processi e dati.", "Outlook sul thread UI, allegati in un processo separato, archivio cifrato nella tray.", "architecture/system.png"),
    ("text", "Scrittura e privacy", "Scrivi, verifica, decidi tu.", "Scrivi, Migliora, Traduci e Oggetto lavorano sulla bozza aperta.", (["Inserisci o sostituisci la selezione", "Le bozze non vengono mai inviate dall'add-in", "Informativa prima dell'uscita dei dati", "Allegati al cloud solo se il profilo lo consente"], ["Informativa accettata dal", "responsabile per le destinazioni.", "Archivio locale cifrato.", "Nessun fallback al cloud."])),
    ("text", "Stato", "Qualifica interna in corso.", "Le prove su dati sintetici non sostituiscono la qualifica su cassette reali.", (["Outlook classico 2016 e successivi", "Windows, Outlook a 32 o 64 bit", ".NET Framework 4.8 e WebView2 Evergreen", "Le risposte IA richiedono verifica"], ["Nessun connettore", "Graph, IMAP o OAuth:", "solo la posta già", "aperta in Outlook."])),
    ("panel", "OnlyWorkspace", "Seleziona. Analizza. Verifica.", "Un add-in per lavorare sul contesto della posta in Outlook classico.", 6),
]


def deck():
    pages = len(DECK)
    for n, (kind, eyebrow, title, sub, body) in enumerate(DECK, 1):
        if kind == "hero":
            content = hero(1280, 720)
        elif kind == "image":
            content = image(0, 0, 1280, 720, OUT / body, 1600, 1040) + text(1250, 40, f"{n:02d} / {pages:02d}", 20, MINT, anchor="end")
        elif kind == "shot":
            from PIL import Image
            content = brand(60, 30) + text(1220, 66, f"{n:02d} / {pages:02d}", 20, MINT, anchor="end")
            content += text(60, 122, title, 34, weight=600) + text(1220, 122, eyebrow.upper(), 17, MINT, 600, anchor="end", spacing=3)
            content += image(60, 142, 1160, 530, OUT / body, *Image.open(OUT / body).size)
            content += text(60, 700, "Build installata su Outlook classico · Cassetta dimostrativa con dati sintetici · Profilo cloud configurato", 17, MUTED)
        else:
            content = brand(60, 40) + text(1220, 76, f"{n:02d} / {pages:02d}", 20, MINT, anchor="end")
            content += text(60, 146, eyebrow.upper(), 17, MINT, 600, spacing=3)
            content += text(60, 206, title, 48, weight=600) + text(60, 252, sub, 22, MUTED)
            if kind == "panel":
                content += panel(body, 60, 278, 1160, 390)
            elif kind == "text":
                left, right = body
                content += rect(60, 300, 720, 340, PANEL, 22) + bullets(96, 370, left, 25, 64)
                content += rect(810, 300, 410, 340, "#16314A", 22) + lines(842, 372, right, 25, gap=48, weight=600)
            note = "Visual dimostrativo · Dati sintetici" if kind == "panel" else "OnlyApps · Outlook classico · Windows"
            content += text(60, 690, note + "  ·  Qualifica interna in corso", 17, MUTED)
        save(f"deck/slide-{n:02d}.svg", 1280, 720, content, title or "OnlyWorkspace", sub or "La tua posta. Un contesto chiaro.")


def one_pager():
    w, h = 1240, 1754
    content = brand(80, 70)
    content += text(80, 230, "La tua posta.", 72, weight=600) + text(80, 316, "Un contesto chiaro.", 72, weight=600)
    content += lines(84, 390, ["Add-in per Outlook classico su Windows: sintesi, ricerca, verifica", "e bozze da controllare, con modelli locali o cloud configurati."], 27, gap=40, fill=MUTED)
    blocks = [
        (80, 500, "Email e scrittura", ["Sintesi e verifica sicurezza", "Scrivi, Migliora, Traduci, Oggetto", "Bozza risposta (mai inviata)", "Attività e appuntamenti", "Estrai dati in CSV"]),
        (640, 500, "Cerca e analizza", ["Riepilogo e punti aperti", "Appuntamenti e scadenze", "Email senza risposta", "Ricostruisci flusso", "Briefing e follow-up"]),
        (80, 920, "IA sotto controllo", ["Ollama locale o profili cloud", "Informativa prima dell'uscita dati", "Web Search per profilo e azione", "Allegati al cloud solo se consentito", "Nessun fallback silenzioso al cloud"]),
        (640, 920, "Archivio locale", ["SQLite cifrato, chiave per utente", "Solo store aperti in Outlook", "Ricerca anche con Outlook chiuso", "Originali da Outlook disponibile", "Limitabile per cassetta"]),
    ]
    for x, y, title, items in blocks:
        content += rect(x, y, 520, 380, PANEL, 24, LINE) + text(x + 36, y + 66, title, 32, weight=600)
        content += line(x + 36, y + 96, x + 484, y + 96) + bullets(x + 36, y + 152, items, 24, 50)
    content += rect(80, 1340, 1080, 210, "#16314A", 24)
    content += text(116, 1402, "Requisiti", 30, weight=600)
    content += lines(116, 1452, ["Outlook classico 2016 e successivi, a 32 o 64 bit · Windows x64", ".NET Framework 4.8 · WebView2 Evergreen · nessun connettore Graph/IMAP/OAuth"], 23, gap=40, fill=MUTED)
    content += line(80, 1612, 1160, 1612)
    content += text(80, 1662, "OnlyApps · Software proprietario · Qualifica interna in corso", 21, MUTED)
    content += text(80, 1702, "Le risposte IA richiedono verifica. Elementi grafici dimostrativi, senza dati reali.", 19, MUTED)
    save("print/scheda-prodotto.svg", w, h, content, "OnlyWorkspace · Scheda prodotto", "Funzioni, controllo dell'IA, archivio locale e requisiti di OnlyWorkspace per Outlook classico.")


ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]


def rasterize(node, modules, sizes):
    """Render the mark at each size; returns {size: (png, bgra_rows_bottom_up)}."""
    js = r'''
const sharp = require(require.resolve('sharp', {paths: [process.argv[3] || process.cwd()]}));
const svg = process.argv[1], out = {};
(async () => {
  for (const n of JSON.parse(process.argv[2])) {
    const img = sharp(Buffer.from(svg.replaceAll('SIZE', n)));
    out[n] = [(await img.clone().png().toBuffer()).toString('base64'), (await img.clone().ensureAlpha().raw().toBuffer()).toString('base64')];
  }
  process.stdout.write(JSON.stringify(out));
})().catch(e => {console.error(e); process.exit(1)});
'''
    source = f'<svg xmlns="http://www.w3.org/2000/svg" width="SIZE" height="SIZE" viewBox="0 0 64 64">{mark(0, 0, 64)}</svg>'
    result = subprocess.run([node, "-e", js, source, json.dumps(sizes), modules or ""], check=True, capture_output=True, text=True).stdout
    frames = {}
    for key, (png, raw) in json.loads(result).items():
        n, rgba = int(key), base64.b64decode(raw)
        rows = [rgba[r * n * 4:(r + 1) * n * 4] for r in range(n)]
        bgra = b"".join(bytes(b for i in range(0, len(row), 4) for b in (row[i + 2], row[i + 1], row[i], row[i + 3])) for row in reversed(rows))
        frames[n] = (base64.b64decode(png), bgra)
    return frames


def ico(frames):
    """ICO with 32-bit DIB frames up to 64 px and a PNG frame at 256, like the Windows shell icons."""
    images = []
    for n, (png, bgra) in sorted(frames.items()):
        if n >= 256:
            images.append(png)
            continue
        mask = bytes(((n + 31) // 32 * 4) * n)
        images.append(struct.pack("<IiiHHIIiiII", 40, n, n * 2, 1, 32, 0, len(bgra) + len(mask), 0, 0, 0, 0) + bgra + mask)
    data = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for n, image in zip(sorted(frames), images):
        data += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(image), offset)
        offset += len(image)
    return data + b"".join(images)


def icons(node, modules):
    frames = rasterize(node, modules, ICON_SIZES)
    (ROOT / "src/OnlyWorkspace.ConfigTool/Assets/OnlyWorkspace.ico").write_bytes(ico(frames))
    for n in (16, 32):
        (ROOT / f"src/OnlyWorkspace.Outlook/UI/ribbon-icon-{n}.png").write_bytes(frames[n][0])


def render(node, modules):
    js = r'''
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const sharp = require(require.resolve('sharp', {paths: [process.argv[2] || process.cwd()]}));
async function run(dir) {
  for (const entry of fs.readdirSync(dir, {withFileTypes:true})) {
    const file = path.join(dir, entry.name);
    if (entry.isDirectory()) await run(file);
    else if (file.endsWith('.svg')) {
      const png = await sharp(file, {density: path.basename(dir) === 'deck' || path.basename(dir) === 'print' ? 144 : 72}).png().toBuffer();
      const output = file.slice(0,-4)+'.png';
      const temporary = output+'.'+crypto.randomUUID()+'.tmp';
      try {
        fs.writeFileSync(temporary, png, {flag: 'wx'});
        fs.renameSync(temporary, output);
      } finally {
        if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
      }
      try { fs.unlinkSync(file); } catch(e) {}
    }
  }
}
run(process.argv[1]).catch(e => {console.error(e); process.exit(1)});
'''
    subprocess.run([node, "-e", js, str(OUT), modules or ""], check=True)


def pdf():
    from reportlab.lib.utils import ImageReader
    from reportlab.pdfgen import canvas

    for name, files, size, title in [("deck/onlyworkspace-presentazione.pdf", [f"deck/slide-{n:02d}.png" for n in range(1, len(DECK) + 1)], (960, 540), "OnlyWorkspace - Presentazione"), ("print/onlyworkspace-scheda-prodotto.pdf", ["print/scheda-prodotto.png"], (595.28, 841.89), "OnlyWorkspace - Scheda prodotto")]:
        c = canvas.Canvas(str(OUT / name), pagesize=size)
        c.setTitle(title)
        c.setAuthor("OnlyApps")
        c.setSubject("Funzioni documentate nel repository; qualifica interna in corso")
        for file in files:
            c.drawImage(ImageReader(str(OUT / file)), 0, 0, *size)
            c.showPage()
        c.save()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--node", default="node")
    parser.add_argument("--node-modules", help="Existing directory containing the sharp package")
    args = parser.parse_args()
    save("brand/icon.svg", 256, 256, mark(8, 8, 240), "OnlyWorkspace icon", "Busta bianca con scintilla gialla su riquadro blu.", False)
    save("brand/logo.svg", 860, 160, mark(8, 20, 120) + text(155, 101, "OnlyWorkspace", 76, "#0F3862", 600), "OnlyWorkspace logo", "Icona OnlyWorkspace e nome su sfondo trasparente.", False)
    save("brand/github-social-preview.svg", 1280, 640, hero(1280, 640), "OnlyWorkspace social preview", "Sintesi, ricerca e bozze dentro Outlook classico. Qualifica interna in corso.")
    architecture()
    deck()
    one_pager()
    render(args.node, args.node_modules)
    icons(args.node, args.node_modules)
    pdf()
    
    # Generate live social media assets
    import subprocess, sys
    social_script = Path(__file__).with_name("generate-social-assets.py")
    if social_script.exists():
        subprocess.run([sys.executable, str(social_script)], check=True)
        
    count = len(list(OUT.rglob("*.png")))
    print(json.dumps({"png": count, "pdf_pages": {"deck": len(DECK), "one_pager": 1}, "root": str(OUT)}))


if __name__ == "__main__":
    main()
