"""
Item-name cleanup proposal for MK Building Materials (ISTMBM26) and MK Paints (ISTMP26).

Reads the item exports (items_<db>.tsv, from export.sql) and writes an Excel workbook for review:
one sheet per firm with the current and proposed name, why it changed, whether it needs a human look, possible
duplicates, activity (sold in 12 months, stock) and a retire suggestion; a groups sheet per firm; and the rules.
The reviewer fills the "Approve" / "Own name" / "Keep active" / "Move to group" columns; apply.py (later) reads them
back and saves each approved change through MK's HQ-identical item save.

Principles: keep the shop's dominant habits (size first for plumbing, LTR, ASI/BER), make them consistent, never
guess meaning (unclear cases are marked Review, not changed silently), stay within HQ's 70 characters.
"""
import csv, re, sys, collections, datetime
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.utils import get_column_letter

SRC, OUT = sys.argv[1], sys.argv[2]
TODAY = datetime.date(2026, 11, 10)
COLS = ['id', 'code', 'group', 'name', 'cat', 'gst', 'hsn', 'stock', 'stat', 'lastSale', 'sales12', 'lastPur', 'srate', 'prate', 'gid']

# ---------------------------------------------------------------- vocabulary
# Whole-word spelling fixes, curated from the data (rare spelling next to a common one). Each is a 'spelling' change.
SPELLING = {
    'SHWER': 'SHOWER', 'CEALING': 'CEILING', 'CEALLING': 'CEILING', 'CEALENG': 'CEILING', 'HJANDLE': 'HANDLE', 'ARALDIE': 'ARALDITE',
    'WEATHR': 'WEATHER', 'DISTEMBE': 'DISTEMPER', 'DISTEMBER': 'DISTEMPER', 'BLAK': 'BLACK', 'BRON': 'BROWN', 'CEMEN': 'CEMENT', 'CEMENET': 'CEMENT',
    'BEMD': 'BEND', 'HOPSE': 'HOSE', 'WASING': 'WASHING', 'ASHIRVASD': 'ASHIRVAD', 'BRAS': 'BRASS', 'ELBOE': 'ELBOW', 'BJAJ': 'BAJAJ',
    'IMMERISION': 'IMMERSION', 'WHATER': 'WATER', 'WTER': 'WATER', 'HEASTER': 'HEATER', 'COVR': 'COVER', 'BASION': 'BASIN', 'VAVLVE': 'VALVE',
    'SOKET': 'SOCKET', 'MRDAL': 'MEDAL', 'CUTER': 'CUTTER', 'COUTTEER': 'CUTTER', 'CUTTEIING': 'CUTTING', 'CUTING': 'CUTTING', 'FREAME': 'FRAME',
    'TAPRIA': 'TAPARIA', 'PHILIP': 'PHILIPS', 'PHILIPH': 'PHILIPS', 'PHILPHS': 'PHILIPS', 'PHILPS': 'PHILIPS', 'BUSAH': 'BUSH', 'PLIAIN': 'PLAIN',
    'MECHINE': 'MACHINE', 'FLISH': 'FLUSH', 'IVOEY': 'IVORY', 'METEAL': 'METAL', 'MODUALR': 'MODULAR', 'MODULER': 'MODULAR', 'MODUAR': 'MODULAR',
    'SWITVH': 'SWITCH', 'SQARE': 'SQUARE', 'CCABLE': 'CABLE', 'SPPED': 'SPEED', 'GRAIZA': 'GRAZIA', 'BULD': 'BULB', 'BUILDERE': 'BUILDER',
    'SHINK': 'SINK', 'MASH': 'MESH', 'MEASH': 'MESH', 'MOTAR': 'MOTOR', 'MOTARS': 'MOTORS', 'NIALS': 'NAILS', 'SANATARY': 'SANITARY',
    'EMOULTION': 'EMULSION', 'ENAMIL': 'ENAMEL', 'CEARAMICS': 'CERAMICS', 'VALES': 'VALVES', 'STEL': 'STEEL', 'PILLER': 'PILLAR', 'SPANER': 'SPANNER',
    'SPANERS': 'SPANNERS', 'TINNER': 'THINNER', 'PEIMERS': 'PRIMERS', 'EHINE': 'SHINE', 'COLOURENT': 'COLOURANT', 'COLOURENTS': 'COLOURANTS',
    'BISION': 'BISON', 'MAHAGANY': 'MAHOGANY', 'MAJENTA': 'MAGENTA', 'GRAINTE': 'GRANITE', 'ROLLE': 'ROLL', 'ROLE': 'ROLL', 'ROLES': 'ROLLS',
    'DMPRF': 'DAMPROOF', 'LUMINARS': 'LUMINOUS', 'CREAME': 'CREAM', 'PATTY': 'PUTTY', 'SELENT': 'SEALANT', 'SEALENT': 'SEALANT', 'ALLDROPS': 'ALDROPS',
    'HORZENTAL': 'HORIZONTAL', 'HORIZENTAL': 'HORIZONTAL', 'WOOG': 'WOOD',
    # second pass: every rare word in both firms read by hand; only certain corrections listed
    'ADHSIVE': 'ADHESIVE', 'ALARAM': 'ALARM', 'ALLMINIUM': 'ALUMINIUM', 'AQAMARINE': 'AQUAMARINE', 'BREZE': 'BREEZE', 'BUINDLES': 'BUNDLES',
    'CAPACITER': 'CAPACITOR', 'CHANELS': 'CHANNELS', 'CLEANR': 'CLEANER', 'COLARENTS': 'COLOURANTS', 'CONCEALD': 'CONCEALED', 'CONCREETE': 'CONCRETE',
    'CONECTOR': 'CONNECTOR', 'CYLENDEEICAL': 'CYLINDRICAL', 'DIVERTOR': 'DIVERTER', 'DOUBBLE': 'DOUBLE', 'ELECTORNIC': 'ELECTRONIC', 'EMMERY': 'EMERY',
    'EXGUST': 'EXHAUST', 'EXTERIAL': 'EXTERIOR', 'FAUSET': 'FAUCET', 'FEDSTAL': 'PEDESTAL', 'FLEXBLE': 'FLEXIBLE', 'GLOVESES': 'GLOVES',
    'GODAVRI': 'GODAVARI', 'GODVARI': 'GODAVARI', 'GRAINDING': 'GRINDING', 'HAELLS': 'HAVELLS', 'HAVELLES': 'HAVELLS', 'HATER': 'HEATER', 'HELMAT': 'HELMET',
    'HINGIES': 'HINGES', 'HOLONEX': 'HALONIX', 'INDICATAR': 'INDICATOR', 'INERNAL': 'INTERNAL', 'ISOLATER': 'ISOLATOR', 'KOHI': 'KOHINOOR', 'KOHIN': 'KOHINOOR',
    'KOHINOR': 'KOHINOOR', 'KOHINR': 'KOHINOOR', 'KOHONOR': 'KOHINOOR', 'KRISAHANA': 'KRISHNA', 'KRISHANA': 'KRISHNA', 'LIQID': 'LIQUID', 'MAHAOGANY': 'MAHOGANY',
    'MALTI': 'MULTI', 'MASQUITOE': 'MOSQUITO', 'MIDIUM': 'MEDIUM', 'MONAR': 'MONARCH', 'MONARACH': 'MONARCH', 'NIFING': 'KNIFING', 'PARYY': 'PARRY',
    'PATTEY': 'PUTTY', 'PENCELL': 'PENCIL', 'PIEGEON': 'PIGEON', 'PLIAN': 'PLAIN', 'PODERS': 'POWDERS', 'PRESURE': 'PRESSURE', 'ROLLAR': 'ROLLER',
    'SCILICON': 'SILICONE', 'SCOTH': 'SCOTCH', 'SCRABBER': 'SCRUBBER', 'SCRBBER': 'SCRUBBER', 'SCREEWS': 'SCREWS', 'SCRES': 'SCREWS', 'SELER': 'SEALER',
    'SHHET': 'SHEET', 'STAIGHT': 'STRAIGHT', 'STNAD': 'STAND', 'STRABERRY': 'STRAWBERRY', 'SUPREM': 'SUPREME', 'TARPALIN': 'TARPAULIN', 'TIOLET': 'TOILET',
    'TITANOUM': 'TITANIUM', 'WARIROR': 'WARRIOR', 'WELCRO': 'VELCRO', 'ZIBRA': 'ZEBRA', 'DUMMI': 'DUMMY', 'DUMMIS': 'DUMMY', 'PHIL': 'PHILIPS',
    'PHILI': 'PHILIPS', 'PHILLI': 'PHILIPS', 'CARMIN': 'CARMINE', 'COTTAN': 'COTTON', 'FORWORDING': 'FORWARDING', 'IORY': 'IVORY', 'VOILET': 'VIOLET',
    'COUPLE': 'COUPLER', 'COUPLING': 'COUPLER', 'CUME': 'CUMI', 'DILL': 'DRILL', 'BOSH': 'BUSH', 'HEALTHA': 'HEALTH', 'TWASTE': 'WASTE', 'SWATT': 'WATT',
}
# Glued words: split, not respell.
GLUED = {'MMUPVC': 'MM UPVC', 'MMPVC': 'MM PVC', 'MMCPVC': 'MM CPVC', 'GIBUSH': 'GI BUSH', 'HPSUGUNA': 'HP SUGUNA', 'BRASELBOW': 'BRASS ELBOW',
         'BRASSELBOW': 'BRASS ELBOW', 'BRASSFTA': 'BRASS FTA', 'BRASSMTA': 'BRASS MTA', 'BALLVALVE': 'BALL VALVE', 'LTDENAMIL': 'LTD ENAMEL',
         'REDOXIDE': 'RED OXIDE', 'GOLDYELLOW': 'GOLD YELLOW', 'PUREWHIT': 'PURE WHITE', 'PROOFLIQUID': 'PROOF LIQUID', 'ALUPAINT': 'ALU PAINT',
         'PURPOSELOCK': 'PURPOSE LOCK', 'WATERTANK': 'WATER TANK',
         'ANGLEHOLDER': 'ANGLE HOLDER', 'ANGLEVALVE': 'ANGLE VALVE', 'BALLCOCK': 'BALL COCK', 'BALVALVE': 'BALL VALVE', 'BRASSTEE': 'BRASS TEE',
         'BRASTEE': 'BRASS TEE', 'CPTAP': 'CP TAP', 'CROMCEALING': 'CROM CEILING', 'DECORBRASS': 'DECOR BRASS', 'DISBOARD': 'DIS BOARD', 'DOGFOOD': 'DOG FOOD',
         'ELEPIPEGOLD': 'ELE PIPE GOLD', 'FULLTURN': 'FULL TURN', 'GODREJNAVATHAL': 'GODREJ NAVATAL', 'GUMTUBE': 'GUM TUBE', 'HANDLEWITH': 'HANDLE WITH',
         'HANDYCUTTERS': 'HANDY CUTTERS', 'HOSECOLLAR': 'HOSE COLLAR', 'INLETPIPE': 'INLET PIPE', 'INLETPIPPPE': 'INLET PIPE', 'MMELE': 'MM ELE',
         'MMELEPIPE': 'MM ELE PIPE', 'MMELEPIPEGOLD': 'MM ELE PIPE GOLD', 'MMELEPIPEPREMIUM': 'MM ELE PIPE PREMIUM', 'MMUPV': 'MM UPVC',
         'NANDIPREMIUM': 'NANDI PREMIUM', 'PCCOATED': 'PC COATED', 'PHIOZABLUE': 'PHIROZA BLUE', 'PILLERCOCK': 'PILLAR COCK', 'PINSOCKET': 'PIN SOCKET',
         'PIPEDIAMOND': 'PIPE DIAMOND', 'PIPEKOHINOOR': 'PIPE KOHINOOR', 'PIPEMONARCH': 'PIPE MONARCH', 'PIPENANDI': 'PIPE NANDI', 'PVCDOORS': 'PVC DOOR',
         'PVCMTA': 'PVC MTA', 'PVCPIPE': 'PVC PIPE', 'PVCPIPEKOHINOR': 'PVC PIPE KOHINOOR', 'PVCPLUGS': 'PVC PLUG', 'PVCTAP': 'PVC TAP', 'ROUNDPAD': 'ROUND PAD',
         'SATINHANDLE': 'SATIN HANDLE', 'SHORTBODY': 'SHORT BODY', 'SQMM': 'SQ MM', 'STEPOVER': 'STEP OVER', 'SWANNECK': 'SWAN NECK', 'TANKNIPPLE': 'TANK NIPPLE',
         'UPVCBRASS': 'UPVC BRASS', 'UPVCPIPEMONARACH': 'UPVC PIPE MONARCH', 'WATERHAEATER': 'WATER HEATER', 'WIREMASH': 'WIRE MESH', 'WOODGUM': 'WOOD GUM',
         'PAINTBRUSH': 'PAINT BRUSH', 'PAINTROLER': 'PAINT ROLLER', 'PALECREAME': 'PALE CREAM', 'PREMIUMBASE': 'PREMIUM BASE', 'SHADECARRDS': 'SHADE CARDS',
         'SIGNALRED': 'SIGNAL RED', 'WALLPUTTY': 'WALL PUTTY', 'ELEPIPE': 'ELE PIPE', 'MMPVCREDUCER': 'MM PVC REDUCER', 'BASEBW': 'BASE BW', 'OXBLUE': 'OX BLUE'}
# One spelling per brand (the form the shop already uses most).
BRANDS = {'ASHIR': 'ASHIRVAD', 'ASHI': 'ASHIRVAD', 'ASHIRWAD': 'ASHIRVAD', 'ASIAN': 'ASI', 'BAJA': 'BAJAJ'}
PLUMBING = re.compile(r'\b(PVC|UPVC|CPVC|SWR|GI|HDPE|PPR|MS|PIPE|ELBOW|TEE|BEND|COUPLER|SOCKET|NIPPLE|FTA|MTA|REDUCER|BUSH|UNION|VALVE|CLAMPS?|CAP|PLUG)\b')
PIPE_BRANDS = ['ASHIRVAD', 'PRINCE', 'SURYA', 'SUPREME', 'FINOLEX', 'ASTRAL', 'NANDI', 'SENTO', 'KISAN', 'KOHINOOR', 'MONARCH', 'KRISHNA']
# Product words are written in the singular (the shop's dominant form); item names only, not group names.
SINGULAR = {'PIPES': 'PIPE', 'VALVES': 'VALVE', 'NIPPLES': 'NIPPLE', 'CLAMPS': 'CLAMP', 'BENDS': 'BEND', 'ELBOWS': 'ELBOW', 'TEES': 'TEE',
            'COUPLERS': 'COUPLER', 'SOCKETS': 'SOCKET', 'BUSHES': 'BUSH', 'REDUCERS': 'REDUCER', 'TAPS': 'TAP', 'BULBS': 'BULB', 'HEATERS': 'HEATER',
            'BASINS': 'BASIN', 'CUTTERS': 'CUTTER', 'SPANNERS': 'SPANNER', 'HANDLES': 'HANDLE', 'WHEELS': 'WHEEL', 'FRAMES': 'FRAME', 'DOORS': 'DOOR', 'PLUGS': 'PLUG'}
# The owner's instruction: on 63/75/90/110 MM elbows and tees, ISI is called HEAVY, written before the item word.
HEAVY_SIZES = {'63', '75', '90', '110'}


def clean(name, group=False, allow=None):
    """Return (proposed, changes:set, notes:list). Changes are categories; notes need a human.
    allow: only these change categories are applied (None = all); used for the "automatic fixes only" name."""
    ok = lambda tag: allow is None or tag in allow
    changes, notes = set(), []
    s0 = name
    s = name.replace('\t', ' ')
    if s.strip() != s: changes.add('spaces')
    if re.search(r'  ', s.strip()): changes.add('spaces')
    s = re.sub(r'\s+', ' ', s).strip()
    up = s.upper()
    if up != s: changes.add('capitals')
    s = up
    for a, b in [('“', '"'), ('”', '"'), ('″', '"'), ("''", '"')]:
        if a in s and ok('inch mark'): s = s.replace(a, b); changes.add('inch mark')
    plumbing = bool(PLUMBING.search(s) or re.search(r'\d\s*MM\b', s))
    if plumbing and ok('inch mark') and re.search(r"\d'(?=[\s)X]|$)", s):
        s = re.sub(r"(\d)'(?=[\s)X]|$)", r'\1"', s); changes.add('inch mark')

    def sub(pattern, repl, tag):
        nonlocal s
        if not ok(tag): return
        new = re.sub(pattern, repl, s)
        if new != s: s = new; changes.add(tag)

    # glued words and sizes
    sub(r'\b([UC])\s*[.-]?\s*PVC\b', r'\1PVC', 'spelling')  # U PVC, C-PVC -> UPVC, CPVC (one spelling, so duplicates show)
    sub(r'(\d)\\(\d)', r'\1/\2', 'inch size')  # 1\2" typed for 1/2"
    sub(r'\b([A-Z]{3,})(\d+)\s*(?=(?:MM|LTRS?|LT|ML|KGS?|GMS?|WATTS?)\b)', r'\1 \2', 'split words')  # WATERTANK2000LT
    for a, b in GLUED.items(): sub(r'\b' + a + r'\b', b, 'split words')
    sub(r'(\d)MM(?=\b|UPVC|CPVC|PVC|GI|SWR)', r'\1 MM', 'units')
    sub(r'\bMM(UPVC|CPVC|PVC|GI|SWR)([A-Z]{3,})?\b', lambda m: 'MM ' + m.group(1) + (' ' + m.group(2) if m.group(2) else ''), 'split words')
    sub(r'\b([A-Z]{4,})(\d+/\d+)', r'\1 \2', 'split words')  # POLISH1/2 -> POLISH 1/2
    sub(r'(\d)\s*(?:LTRS|LTR|LT|LITRES|LITRE|LITERS|LITER)\b', r'\1 LTR', 'units')
    if not re.search(r'\bMM\b|LOCK', s): sub(r'(\d)\s*L\b', r'\1 LTR', 'units')  # "PAD LOCK 40 MM 5 L" is 5 levers
    sub(r'(\d)\s*ML\b', r'\1 ML', 'units')
    sub(r'(\d)\s*(?:KGS|KG)\b', r'\1 KG', 'units')
    sub(r'(\d)\s*(?:GMS|GRMS|GRM|GRAMS|GM)\b', r'\1 GM', 'units')
    if 'BASE' not in s: sub(r'(\d)\s*G\b', r'\1 GM', 'units')  # ASI base codes such as AC 17G are not grams
    sub(r'(\d)\s*(?:WATTS|WATT|W)\b', r'\1 WATT', 'units')
    sub(r'(\d)\s*(?:AMPS|AMP)\b', r'\1 AMP', 'units')
    # inch fractions: 1.1/4" 1 .1/2" 11/4" 2.5" -> 1 1/4" 1 1/2" 2 1/2"
    sub(r'(\d)\s*\.\s*(\d/\d)', r'\1 \2', 'inch size')
    sub(r'\b([12])(1/[24]|3/4)"', r'\1 \2"', 'inch size')
    sub(r'(\d)\s*\.5"', r'\1 1/2"', 'inch size')
    sub(r'\b1\.14(?=[*"\s)])', '1 1/4', 'inch size')  # 1.14 typed for 1.1/4
    sub(r'(\d(?: \d)?/?\d*)"\s*(\d/\d)"?', r'\1 \2"', 'inch size')  # 2"1/2" -> 2 1/2"
    # reducer / size multiplication, written with * (the owner types it on the numeric keypad): 25 X 15 MM -> 25*15 MM
    sub(r'(?<=[\d"])\s*[*xX]\s*(?=\d)', '*', 'sizes')
    sub(r'(\d)([A-Z]{4,})\b', r'\1 \2', 'split words')  # 13.5COLD -> 13.5 COLD
    # brackets: one space before, none inside, no doubles
    sub(r'\(\s*\(', '(', 'brackets'); sub(r'\)\s*\)', ')', 'brackets')
    sub(r'\(\s+', '(', 'brackets'); sub(r'\s+\)', ')', 'brackets')
    sub(r'(?<=[^\s(])\(', ' (', 'brackets'); sub(r'\)(?=[^\s)])', ') ', 'brackets')
    # trailing bare inch on a plumbing item goes into brackets: 25 MM CPVC ELBOW 1" -> 25 MM CPVC ELBOW (1")
    INCH = r'(?:\d+(?: \d/\d)?|\d/\d)'
    plumbing = bool(PLUMBING.search(s) or re.search(r'\d\s*MM\b', s))  # again: glued words are split by now
    if plumbing: sub(r'(\d)\s+"', r'\1"', 'inch mark')  # ( 2 '') -> (2")
    if plumbing and re.search(r'\d\s*MM\b', s):
        sub(r'(?<=\s)(' + INCH + r'(?:\*' + INCH + r')?)"$', r'(\1")', 'inch size')

        # a bracket holding only inch sizes gets its inch mark and fractions: (3/4) (2.5X1.5) -> (3/4") (2 1/2X1 1/2")
        def inch_bracket(m):
            return '(' + re.sub(r'(?<![\d.])(\d)\.5(?!\d)', r'\1 1/2', m.group(1)) + '")'
        size = r'(?:\d+(?:\.5)?(?: \d/\d)?|\d/\d)'
        sub(r'\((' + size + r'(?:\*' + size + r')?)\)', inch_bracket, 'inch size')
    # words: spelling and brands
    words = s.split(' ')
    for i, w in enumerate(words):
        core = re.sub(r'[^A-Z]', '', w)
        if core in SPELLING and core == w.strip('()') and ok('spelling'):
            words[i] = w.replace(core, SPELLING[core]); changes.add('spelling')
        elif core in BRANDS and core == w.strip('()') and ok('brand'):
            words[i] = w.replace(core, BRANDS[core]); changes.add('brand')
        elif not group and core in SINGULAR and core == w.strip('()') and ok('singular') and not re.search(r'\b(INDUSTRIES|TRADERS|LTD|PVT|&)\b|&', s):
            words[i] = w.replace(core, SINGULAR[core]); changes.add('singular')
    s = ' '.join(words)
    if group: return re.sub(r'\s+', ' ', s).strip(), changes, notes
    # the owner's rule: 63/75/90/110 MM elbows and tees say HEAVY (not ISI), before the item word: 75 MM PVC HEAVY ELBOW
    m = re.match(r'^(\d+) MM ', s)
    if m and ok('ISI to HEAVY') and m.group(1) in HEAVY_SIZES and re.search(r'\b(ELBOW|TEE)\b', s) and re.search(r'\b(ISI|HEAVY)\b', s):
        t = re.sub(r'\s*\b(ISI|HEAVY)\b', '', s)
        t = re.sub(r'\s+', ' ', re.sub(r'\b(ELBOW|TEE)\b', r'HEAVY \1', t, count=1)).strip()
        if t != s: s = t; changes.add('ISI to HEAVY')
    # plumbing: size first, brand last ("ASHIRVAD BORE PIPE 32 MM (1 1/4")" -> "32 MM BORE PIPE (1 1/4") ASHIRVAD")
    if ok('order') and PLUMBING.search(s) and not re.match(r'^\d', s):
        sizes = list(re.finditer(r'(?<=\s)(\d+(?:\*\d+)? MM)\b', s))
        if len(sizes) == 1:
            m = sizes[0]
            pre, post = s[:m.start()].split(), s[m.end():].strip()
            brand = [w for w in pre if w in PIPE_BRANDS]
            rest = [w for w in pre if w not in PIPE_BRANDS]
            s = ' '.join([m.group(1)] + rest + ([post] if post else []) + brand); changes.add('order')
    # plumbing: a pipe brand goes last, in brackets (the shop's dominant form: "... (4") (PRINCE)")
    if ok('brand') and PLUMBING.search(s) and re.match(r'^\d', s):
        brands = '|'.join(PIPE_BRANDS)
        t = re.sub(r' (' + brands + r') (\([^()]*"\))$', r' \2 (\1)', s)          # ... PIPE MONARCH (3/4") -> ... PIPE (3/4") (MONARCH)
        t = re.sub(r' (' + brands + r')$', r' (\1)', t)                          # ... PIPE SURYA -> ... PIPE (SURYA)
        if t != s: s = t; changes.add('brand')
    # paints: the brand comes first, as in "ASI EN ...": EN BER LEAF BROWN -> BER EN LEAF BROWN
    m = re.match(r'^(EN|ENA|EML) (BER|ASI|ICI|DULUX|SH|BISON) (.*)$', s)
    if m and ok('paint order'): s = f'{m.group(2)} {m.group(1)} {m.group(3)}'; changes.add('paint order')
    # lighting: wattage last (WIPRO 15 WATT CFL -> WIPRO CFL 15 WATT)
    if ok('watt last') and re.search(r'\b(LED|CFL|BULB|TUBE|LIGHT|LAMP)\b', s):
        m = re.search(r' (\d+(?:\.\d+)?) WATT\b', s)
        if m and not s.endswith(m.group(0)) and '(' not in s[m.end():]:
            t = re.sub(r'\s+', ' ', s[:m.start()] + s[m.end():]).strip() + m.group(0)
            if t != s: s = t; changes.add('watt last')
    # brand in brackets at the end, with no other bracket meaning: keep brackets (shop habit), just tidy
    s = re.sub(r'\s+', ' ', s).strip()

    pipework = bool(PLUMBING.search(s))
    if pipework and re.match(r'^\d+(?: \d/\d)?"', s): notes.append('size in inches only; add the MM size?')
    if pipework and re.search(r'\d\s*MM\b', s) and re.search(r'\s\d+$', s) and '"' not in s: notes.append('ends with a bare number: inch size?')
    if len(s) > 70: notes.append(f'{len(s)} characters; HQ allows 70')
    return s, changes, notes


# The owner's decision: spelling, capitals and spaces need no approval (nor his own ISI -> HEAVY rule).
AUTO = {'spaces', 'capitals', 'spelling', 'split words', 'ISI to HEAVY'}
# Formatting the owner approves in bulk (he has opinions on these, e.g. * not X).
TIDY = {'units', 'brackets', 'inch mark', 'inch size', 'sizes', 'singular', 'brand', 'paint order', 'watt last'}
MATERIALS = r'(UPVC|CPVC|PVC|GI|SWR|HDPE|MS)'


def learn_sizes(rows):
    # This shop's own mm<->inch pairs per material (25 MM PVC is 3/4" here, 25 MM CPVC is 1"): a pair counts when at
    # least two items agree and 80% of the items with that size say the same.
    votes = collections.defaultdict(collections.Counter)
    for r in rows:
        m = re.match(r'^(\d+) MM ' + MATERIALS + r'\b.*\((\d+(?: \d/\d)?|\d/\d)"\)', r['new'])
        if m: votes[(m.group(2), m.group(1))][m.group(3)] += 1
    mm_inch = {}
    for k, c in votes.items():
        inch, n = c.most_common(1)[0]
        if n >= 2 and n / sum(c.values()) >= 0.8: mm_inch[k] = inch
    by_inch = collections.defaultdict(set)
    for (mat, mm), inch in mm_inch.items(): by_inch[(mat, inch)].add(mm)
    return mm_inch, {k: next(iter(v)) for k, v in by_inch.items() if len(v) == 1}


def refine(r, sizes):
    mm_inch, inch_mm = sizes
    s = r['new']
    m = re.match(r'^(\d+) MM ' + MATERIALS + r'\b(.*) (\d+(?: \d/\d)?|\d/\d)$', s)
    if m and mm_inch.get((m.group(2), m.group(1))) == m.group(4):
        r['new'] = f'{m.group(1)} MM {m.group(2)}{m.group(3)} ({m.group(4)}")'
        r['changes'].add('inch size')
        r['notes'] = [n for n in r['notes'] if not n.startswith('ends with a bare number')]
    m = re.match(r'^(\d+(?: \d/\d)?|\d/\d)" ' + MATERIALS + r' (.*)$', s)
    if m and (m.group(2), m.group(1)) in inch_mm:
        r['new'] = f'{inch_mm[(m.group(2), m.group(1))]} MM {m.group(2)} {m.group(3)} ({m.group(1)}")'
        r['changes'].add('order')
        r['notes'] = [n for n in r['notes'] if not n.startswith('size in inches only')] + ["MM size added from the shop's own items of this size"]


# Product families, to spot items filed in an unrelated group (a suggestion only).
FAMILIES = [
    ('CPVC', r'\bCPVC\b'), ('UPVC', r'\bUPVC\b'), ('SWR', r'\bSWR\b'), ('GI', r'\bGI\b'),
    ('PVC', r'\bPVC\b.*\b(PIPE|ELBOW|TEE|BEND|COUPLER|SOCKET|MTA|FTA|REDUCER|BUSH|CAP|PLUG|UNION|NIPPLE|DOOR|JOINT|CLAMP)\b'),
    ('PAINT', r'\b(EMULSION|ENAMEL|ENA|PRIMER|PUTTY|DISTEMPER|THINNER|COLOURANTS?|WEATHER|EML|PAINT|POLISH|VARNISH|STAINER|RED OXIDE|WALL CARE|WALLCARE)\b'
              r'|^(BER|ASI|EN|ICI|DULUX|SH|D D L|BISON|SHEEN|NEROLAC|JK)\b'),
    ('FAN', r'\bFANS?\b'), ('HEATER', r'\b(WATER HEATER|GEYSER|IMMERSION|HEATER)\b'),
    ('LIGHTING', r'\b(LED|CFL|BULB|TUBE LIGHT|TUBE SET|LIGHT|LAMP|HALONIX)\b'),
    ('ELECTRICAL', r'\b(WIRE|CABLE|SWITCH|MCB|HOLDER|INDICATOR|REGULATOR|ELE PIPE|ELE|SQ MM|MODULAR|SOCKET|PLUG TOP|BOARD|AMP|DB|ISOLATOR|CHANGEOVER|EXTENSION)\b'),
    ('TANK', r'\bWATER TANK\b|\b\d+ LTR\b.*\bTANK\b'), ('MOTOR', r'\b(MOTOR|MOTORS|PUMP|MONOBLOCK|SUBMERSIBLE|\d+(?:\.\d+)? HP)\b'),
    ('TAP / SANITARY', r'\b(TAP|COCK|SHOWER|BASIN|SINK|CLOSET|EWC|FAUCET|SPOUT|DIVERTER|URINAL|PILLAR|BIB|ANGLE VALVE|WASTE|FLUSH|HEALTH|CISTERN|SPINDLE|MIRROR|TOWEL|SOAP)\b'),
    ('ADHESIVE', r'\b(ARALDITE|FEVICOL|FEVIKWIK|M SEAL|SEALANT|SOLVENT|ANABOND)\b'),
    ('ABRASIVE', r'\b(EMERY|SAND PAPER|WATER PAPER|CUTTING WHEEL|GRINDING)\b'),
    ('TOOLS / HARDWARE', r'\b(LOCK|PADLOCK|HINGES?|BOLTS?|NAILS?|SCREWS?|DRILL|BITS?|BLADE|SPANNER|HAMMER|CUTTER|PLIER|ALDROPS?|HANDLE|HOOKS?|CHAIN|WHEEL|TAPE|BRUSH|ROLLER|GLOVES|'
                         r'TOWER BOLT|DOOR STOPPER|CLAMP|WASHERS?|FILE|SAW|HACKSAW|TROWEL|LEVEL|MEASURING|SCREWDRIVER|WRENCH|CHISEL|GRILL|MESH|ROPE|STOOL|LADDER)\b'),
]


def family(name):
    return next((f for f, p in FAMILIES if re.search(p, name)), None)


def load(db):
    rows = []
    with open(f'{SRC}\\items_{db}.tsv', encoding='utf-8-sig', errors='replace') as f:
        for line in f:
            p = line.rstrip('\n').split('\t')
            if len(p) >= 15: rows.append(dict(zip(COLS, p)))
    return rows


def num(v):
    try: return float(v)
    except ValueError: return 0.0


def day(v):
    return None if v in ('NULL', '', None) else datetime.date.fromisoformat(v.strip())


def propose(db):
    rows = load(db)
    for r in rows:
        r['new'], r['changes'], r['notes'] = clean(r['name'])
        r['group_new'], gch, _ = clean(r['group'], group=True)
        r['group_changed'] = r['group_new'] != r['group'].strip()
    sizes = learn_sizes(rows)
    for r in rows: refine(r, sizes)
    # Misfiled items: an item whose product family differs from the clear family of its group, when another group is
    # clearly that family. A suggestion only; brand groups with mixed items are left alone.
    fam = {r['id']: family(r['new']) for r in rows}
    members = collections.defaultdict(list)
    for r in rows: members[r['group'].strip()].append(fam[r['id']])
    dominant = {}
    for g, fs in members.items():
        known = [f for f in fs if f]
        if len(known) >= 5:
            top, n = collections.Counter(known).most_common(1)[0]
            if n / len(fs) >= 0.75: dominant[g] = top
    home = {}
    for g, f in dominant.items():
        here = members[g].count(f)
        if f not in home or here > members[home[f]].count(f): home[f] = g
    for r in rows:
        f, g = fam[r['id']], r['group'].strip()
        r['family'] = f or ''
        r['suggestGroup'] = home[f] if f and g in dominant and dominant[g] != f and f in home and home[f] != g else ''
    # Two items read the same when their letters and digits match ignoring spaces ("AH10" = "AH 10") or when they have
    # the same words in another order ("LED BULB 5 WATT" = "5 WATT LED BULB").
    keys = [lambda r: re.sub(r'[^A-Z0-9/"]', '', r['new']), lambda r: ' '.join(sorted(re.findall(r'[A-Z0-9/"]+', r['new'])))]
    alike = collections.defaultdict(dict)
    for key in keys:
        by_name = collections.defaultdict(list)
        for r in rows: by_name[key(r)].append(r)
        for same in by_name.values():
            for r in same:
                for o in same:
                    if o is not r: alike[r['id']][o['id']] = o
    for r in rows:
        if alike[r['id']]:
            others = list(alike[r['id']].values())
            if True:
                r['dup'] = '; '.join(f"{o['id']} {o['new']} ({o['group'].strip()}, sold {o['sales12']}x)" for o in others)
                if any(o['group'] == r['group'] and o['new'] == r['new'] for o in others):
                    r['notes'].append('another item in this group would get the same name: HQ refuses that; decide which to keep')
                elif any(o['group'] == r['group'] for o in others):
                    r['notes'].append('looks like a duplicate of another item in this group')
    # GST and HSN against similar items: the rate most items with the same HSN (first 4 digits) carry, and the HSN most
    # items of the same kind carry. Flags only; the owner decides.
    by_hsn = collections.defaultdict(collections.Counter)
    for r in rows:
        if len(r['hsn']) >= 4: by_hsn[r['hsn'][:4]][r['gst']] += 1
    # "the same kind": same family and the same first two words without numbers (ASI EN ..., 25 MM CPVC ELBOW -> CPVC ELBOW)
    kind = lambda r: (r['family'], ' '.join([w for w in r['new'].split() if not re.search(r'\d', w) and w != 'MM'][:2]))
    by_kind = collections.defaultdict(collections.Counter)
    by_fam = collections.defaultdict(collections.Counter)
    for r in rows:
        if re.fullmatch(r'\d{4}|\d{6}|\d{8}', r['hsn']):
            by_kind[kind(r)][r['hsn']] += 1
            if r['family']: by_fam[r['family']][r['hsn']] += 1
    def majority(counter, share):
        if not counter: return None
        value, n = counter.most_common(1)[0]
        return value if n >= 3 and n / sum(counter.values()) >= share else None
    for r in rows:
        checks, r['hsnSuggest'] = [], ''
        if not re.fullmatch(r'\d{4}|\d{6}|\d{8}', r['hsn']):
            checks.append('HSN missing' if not r['hsn'] else f'HSN {r["hsn"]} is not 4, 6 or 8 digits')
            r['hsnSuggest'] = majority(by_kind[kind(r)], 0.6) or (majority(by_fam[r['family']], 0.7) if r['family'] else None) or ''
        elif r['family']:
            # an HSN whose digits are the family's usual code shuffled (1397 for 3917) is a typing slip
            fam4 = majority(collections.Counter(h[:4] for h, n in by_fam[r['family']].items() for _ in range(n)), 0.7)
            if fam4 and r['hsn'][:4] != fam4 and sorted(r['hsn'][:4]) == sorted(fam4):
                checks.append(f'HSN {r["hsn"]} looks like {fam4} typed wrong')
                r['hsnSuggest'] = majority(by_kind[kind(r)], 0.6) or majority(by_fam[r['family']], 0.5) or fam4
        usual = majority(by_hsn[r['hsn'][:4]], 0.8) if len(r['hsn']) >= 4 else None
        if usual and usual != r['gst']: checks.append(f'GST {float(r["gst"]):g}% but most items with HSN {r["hsn"][:4]} are {float(usual):g}%')
        elif float(r['gst'] or 0) == 0 and r['family'] and majority(collections.Counter(o['gst'] for o in rows if o['family'] == r['family']), 0.8) not in (None, r['gst']):
            checks.append(f'GST 0% while other {r["family"].lower()} items charge GST')
        sold = r['sales12'] != '0'
        if sold and num(r['srate']) == 0: checks.append('sale rate is 0')
        elif sold and num(r['srate']) < num(r['prate']): checks.append(f'sale rate {num(r["srate"]):g} below purchase rate {num(r["prate"]):g}')
        r['dataCheck'] = '; '.join(checks)
    for r in rows:
        last_sale, last_pur, stock = day(r['lastSale']), day(r['lastPur']), num(r['stock'])
        idle = r['sales12'] == '0' and (last_pur is None or (TODAY - last_pur).days > 365)
        r['retire'] = 'Yes' if idle and stock <= 0 and r['stat'] == '0' else ''
        if r['stat'] != '0': r['retire'] = 'already inactive'
        r['stockNote'] = f'negative stock {stock:g}: check in HQ' if stock < 0 else ''
        changed = r['new'] != r['name'].strip() or r['name'] != r['name'].strip()
        r['changed'] = changed
        r['check'] = ('' if not changed and not r['notes'] else 'Review' if r['notes'] or (r['changes'] - AUTO - TIDY)
                      else 'Automatic' if r['changes'] <= AUTO else 'Tidy-up')
    return rows


# ---------------------------------------------------------------- workbook
HEAD = Font(bold=True, color='FFFFFF')
HEAD_FILL = PatternFill('solid', fgColor='0F766E')
NEW_FILL = PatternFill('solid', fgColor='E0F2EF')
REVIEW_FILL = PatternFill('solid', fgColor='FFF4D6')
INPUT_FILL = PatternFill('solid', fgColor='EEF2FF')
DEAD_FONT = Font(color='8A8A8A')


def sheet(wb, title, headers, widths, rows, inputs=()):
    ws = wb.create_sheet(title)
    ws.append(headers)
    for i, h in enumerate(headers, 1):
        c = ws.cell(row=1, column=i)
        c.font, c.alignment = HEAD, Alignment(wrap_text=True, vertical='top')
        c.fill = INPUT_FILL if h in inputs else HEAD_FILL
        if h in inputs: c.font = Font(bold=True, color='1E3A8A')
        ws.column_dimensions[get_column_letter(i)].width = widths[i - 1]
    for r in rows: ws.append(r)
    ws.freeze_panes = 'E2'
    ws.auto_filter.ref = ws.dimensions
    ws.row_dimensions[1].height = 32
    return ws


def build(dbs):
    wb = Workbook()
    readme = wb.active
    readme.title = 'How to review'
    lines = [
        ('Item names: proposed clean-up', True),
        ('Nothing has been changed in HQ. This sheet is a proposal; after review, MK applies only what is approved, one item at a time, '
         'exactly as HQ\'s own Items screen saves it. Old names are kept, so any change can be undone.', False),
        ('', False),
        ('What to do (blue columns are yours)', True),
        ('Approve: Y = use the proposed name, N = keep the current name. Blank = not decided yet. Automatic rows are already Y (change to N to refuse one).', False),
        ('Own name: type a better name here to use it instead (overrides the proposal).', False),
        ('Keep active: N = hide the item from bills (it keeps its history and can be switched back on). Suggested for items not sold or bought for a year with no stock.', False),
        ('Move to group: type the group the item should belong to (see the Groups sheets).', False),
        ('Planning groups: the Families sheets show each kind of product and the groups it is scattered over today '
         '(PVC fittings sit in 6 groups, taps in 18). Write the group each family should go to; MK then fills "Move to group" for those items.', False),
        ('Tip: filter "Check" = Tidy-up and type Y down the Approve column in one go; look at "Review" rows one by one.', False),
        ('', False),
        ('The standard', True),
        ('1. CAPITALS, single spaces.', False),
        ('2. Plumbing: size first, then material, variant, item, inch size in brackets, brand last:  25 MM CPVC ELBOW (1")   75 MM PVC HEAVY TEE   25*15 MM UPVC REDUCER (1*1/2")   32 MM BORE PIPE (1 1/4") ASHIRVAD', False),
        ('   63, 75, 90 and 110 MM elbows and tees: HEAVY instead of ISI (75 MM PVC HEAVY ELBOW).', False),
        ('   An item named only in inches gets its MM size from the shop\'s own items of that size (1" CPVC ELBOW -> 25 MM CPVC ELBOW (1")). 25 MM PVC is 3/4" here, 25 MM CPVC is 1".', False),
        ('3. Inch marks always ", fractions written 1 1/4", 1 1/2", 2 1/2" (not 1.1/4, 11/4, 2.5 or 2"1/2").', False),
        ('4. Units with one space: 4 LTR, 500 ML, 20 KG, 200 GM, 9 WATT, 16 AMP, 40 MM.', False),
        ('5. Sizes multiplied with * (numeric keypad): 25*15 MM, 14*16 (not 25X15 or 25 * 15). Product words in the singular: NIPPLE, CLAMP, PIPE.', False),
        ('   Pipe brands last, in brackets: 20 MM UPVC PIPE (3/4") (MONARCH). Paints: brand first, as ASI EN ...: BER EN LEAF BROWN 1 LTR. Lighting: wattage last: WIPRO CFL 15 WATT.', False),
        ('6. One spelling per word and brand: SINK (not SHINK), CEILING, DISTEMPER, PILLAR, SPANNER, THINNER, ENAMEL, ASHIRVAD, PHILIPS, TAPARIA.', False),
        ('7. The shop\'s short forms stay (ASI, BER, EN, LTR) so searching works as before.', False),
        ('8. At most 70 characters (HQ\'s limit); no two items with the same name in one group (HQ refuses).', False),
        ('', False),
        ('Columns', True),
        ('Check: Automatic = only spelling, capitals, spaces or ISI -> HEAVY: applied without asking (pre-filled Y). Tidy-up = formatting (units, brackets, inch sizes, *, singular): approve in bulk. Review = words reordered, a size added, or a decision needed (see Notes).', False),
        ('GST / HSN / price check: HSN missing or not 4/6/8 digits (GST invoices need it), a GST rate different from most items with the same HSN, '
         'or a sold item priced at 0 or below its purchase rate. "Suggested HSN" is the code most items of the same kind carry. '
         'To fix, type the right value in "Set HSN" / "Set GST %"; MK saves it exactly as HQ\'s Items screen does.', False),
        ('Product family: what kind of item it is (CPVC, PVC, paint, electrical, tap / sanitary...). Sort by it to plan new groups.', False),
        ('Suggested group: the item clearly belongs with another group\'s items (for example a PVC fitting filed under CPVC). Copy it to "Move to group" if right.', False),
        ('Stock warning: HQ shows negative stock (sold more than was entered as bought). Not a naming issue; worth correcting in HQ.', False),
        ('Possibly the same item as: after cleaning, another item reads the same. Likely a duplicate: keep the one that sells, retire the other (items cannot be merged; each keeps its own history).', False),
        ('Retire?: Yes = no sale and no purchase for 12 months and no stock.', False),
    ]
    for text, bold in lines:
        readme.append([text])
        readme.cell(row=readme.max_row, column=1).font = Font(bold=bold, size=13 if bold else 11)
        readme.cell(row=readme.max_row, column=1).alignment = Alignment(wrap_text=True)
    readme.column_dimensions['A'].width = 140

    summary = []
    for db, label in dbs:
        rows = propose(db)
        rows.sort(key=lambda r: (r['group_new'], r['new']))
        headers = ['Item ID', 'Code', 'Group', 'Current name', 'Proposed name', 'Check', 'What changed', 'Notes', 'Possibly the same item as',
                   'Sold (12 months)', 'Last sale', 'Last purchase', 'Stock', 'Stock warning', 'Sale rate', 'Retire?', 'Product family', 'Suggested group',
                   'GST / HSN / price check', 'HSN', 'GST %', 'Suggested HSN',
                   'Approve (Y/N)', 'Own name', 'Keep active (Y/N)', 'Move to group', 'Set HSN', 'Set GST %']
        inputs = headers[22:]
        data = []
        for r in rows:
            data.append([int(r['id']), r['code'], r['group'].strip(), r['name'].strip(), r['new'] if r['changed'] else '', r['check'],
                         ', '.join(sorted(r['changes'])), '; '.join(r['notes']), r.get('dup', ''), int(r['sales12']),
                         day(r['lastSale']), day(r['lastPur']), num(r['stock']), r['stockNote'], num(r['srate']), r['retire'], r['family'], r['suggestGroup'],
                         r['dataCheck'], r['hsn'], float(r['gst'] or 0), r['hsnSuggest'],
                         'Y' if r['check'] == 'Automatic' else '', '', '', '', '', ''])
        ws = sheet(wb, f'{label} items', headers, [8, 9, 22, 42, 42, 10, 22, 30, 30, 8, 11, 11, 8, 16, 9, 9, 14, 22, 34, 10, 7, 11, 10, 30, 10, 20, 10, 9], data, inputs)
        for i, r in enumerate(rows, start=2):
            if r['changed']: ws.cell(row=i, column=5).fill = NEW_FILL
            if r['check'] == 'Review': ws.cell(row=i, column=6).fill = REVIEW_FILL
            if r['retire'] == 'Yes':
                for c in range(1, 16): ws.cell(row=i, column=c).font = DEAD_FONT
            for c in (11, 12): ws.cell(row=i, column=c).number_format = 'DD-MM-YYYY'

        groups = collections.defaultdict(lambda: {'n': 0, 'sold': 0, 'retire': 0, 'id': ''})
        for r in rows:
            g = groups[r['group'].strip()]
            g['n'] += 1; g['sold'] += r['sales12'] != '0'; g['retire'] += r['retire'] == 'Yes'; g['id'] = r['gid']; g['new'] = r['group_new']
        gdata = [[int(v['id'] or 0), k, v['new'] if v['new'] != k else '', v['n'], v['sold'], v['retire'], '', ''] for k, v in sorted(groups.items())]
        sheet(wb, f'{label} groups', ['Group ID', 'Current group', 'Cleaned spelling', 'Items', 'Sold in 12 months', 'Suggested to retire', 'New group name', 'Merge into'],
              [9, 34, 34, 8, 10, 10, 30, 30], gdata, ['New group name', 'Merge into'])
        export(db, label, rows, globals().get('ANSWERS', {}).get(label, {}))
        # Where each kind of product lives today: the map for redesigning groups.
        spread = collections.defaultdict(collections.Counter)
        for r in rows: spread[r['family'] or '(other)'][r['group'].strip()] += 1
        fdata = []
        for f, c in sorted(spread.items(), key=lambda kv: -sum(kv[1].values())):
            sold = sum(1 for r in rows if (r['family'] or '(other)') == f and r['sales12'] != '0')
            fdata.append([f, sum(c.values()), sold, len(c), '; '.join(f'{g} ({n})' for g, n in c.most_common(8)) + ('; …' if len(c) > 8 else ''), ''])
        sheet(wb, f'{label} families', ['Product family', 'Items', 'Sold in 12 months', 'Spread over groups', 'Main groups today (items)', 'Proposed group'],
              [16, 8, 10, 10, 120, 30], fdata, ['Proposed group'])
        changed = sum(r['changed'] for r in rows)
        count = lambda c: sum(r['check'] == c for r in rows)
        summary.append((label, len(rows), changed, count('Automatic'), count('Tidy-up'), count('Review'),
                        sum(1 for r in rows if r.get('dup')), sum(r['retire'] == 'Yes' for r in rows), sum(1 for r in rows if r['suggestGroup']), len(groups),
                        sum(1 for r in rows if r['dataCheck'] and r['retire'] != 'Yes')))
    readme.append([''])
    readme.append(['Summary']); readme.cell(row=readme.max_row, column=1).font = Font(bold=True, size=13)
    for label, n, ch, auto, tidy, rev, dup, ret, sg, g, dc in summary:
        readme.append([f'{label}: {n} items, {ch} names would change ({auto} automatic, {tidy} tidy-ups to approve, {rev} to review), '
                       f'{dup} read the same as another item, {ret} suggested to retire, {sg} look misfiled, {g} groups; '
                       f'{dc} items still in use have a GST / HSN / price point to check.'])
    wb.save(OUT)
    return summary


RULES = {
    'spaces': 'Extra spaces removed', 'capitals': 'Written in capitals', 'spelling': 'Spelling corrected', 'split words': 'Joined words separated',
    'ISI to HEAVY': '63/75/90/110 MM elbows and tees: HEAVY instead of ISI',
    'units': 'Units written one way: 4 LTR, 500 ML, 20 KG, 700 GM, 9 WATT', 'brackets': 'Brackets tidied: one space before, none inside',
    'inch mark': 'Inch mark always "', 'inch size': 'Inch sizes written 1 1/4", in brackets after the item', 'sizes': 'Sizes multiplied with *: 25*15 MM',
    'singular': 'Product words in the singular: NIPPLE, CLAMP, PIPE', 'brand': 'One spelling per brand; pipe brands last in brackets',
    'paint order': 'Paints: brand first (BER EN ..., as ASI EN ...)', 'watt last': 'Lighting: wattage last (WIPRO CFL 15 WATT)',
    'order': 'Words reordered: size first',
}

ANSWER_COLUMNS = {'Approve (Y/N)': 'approve', 'Own name': 'ownName', 'Keep active (Y/N)': 'keepActive', 'Move to group': 'moveToGroup',
                  'Set HSN': 'setHsn', 'Set GST %': 'setGst'}


def read_answers(path):
    """The owner's answers from a filled-in review sheet: {label: {item id: {approve, ownName, ...}}}."""
    from openpyxl import load_workbook
    wb = load_workbook(path, read_only=True, data_only=True)
    out = {}
    for ws in wb.worksheets:
        if not ws.title.endswith(' items'): continue
        rows = ws.iter_rows(values_only=True)
        head = list(next(rows))
        col = {name: head.index(name) for name in ['Item ID', *ANSWER_COLUMNS] if name in head}
        answers = {}
        for row in rows:
            if row[col['Item ID']] is None: continue
            a = {key: str(row[col[name]]).strip() for name, key in ANSWER_COLUMNS.items() if name in col and row[col[name]] not in (None, '')}
            if a: answers[int(row[col['Item ID']])] = a
        out[ws.title[:-len(' items')]] = answers
    return out


def export(db, label, rows, answers):
    """proposals-<label>.json: what MK's guided review screen works from."""
    import json
    items = []
    for r in rows:
        items.append({
            'id': int(r['id']), 'code': r['code'], 'group': r['group'].strip(), 'groupId': int(r['gid'] or 0), 'current': r['name'].strip(),
            'proposed': r['new'] if r['changed'] else None, 'check': r['check'] or None,
            'autoName': (lambda a: a if a != r['name'].strip() else None)(clean(r['name'], allow=AUTO)[0]) if r['changed'] else None, 'changes': sorted(r['changes']) if r['changed'] else [],
            'notes': r['notes'], 'duplicateOf': [int(x) for x in re.findall(r'(?:^|; )(\d+) ', r.get('dup', ''))], 'sold12': int(r['sales12']),
            'lastSale': r['lastSale'].strip() if r['lastSale'] != 'NULL' else None, 'lastPurchase': r['lastPur'].strip() if r['lastPur'] != 'NULL' else None,
            'stock': num(r['stock']), 'saleRate': num(r['srate']), 'retire': r['retire'] == 'Yes', 'active': r['stat'] == '0', 'family': r['family'] or None,
            'suggestGroup': r['suggestGroup'] or None, 'dataCheck': r['dataCheck'] or None, 'hsn': r['hsn'], 'gst': float(r['gst'] or 0),
            'hsnSuggest': r['hsnSuggest'] or None, 'answers': answers.get(int(r['id'])),
        })
    path = OUT.rsplit('\\', 1)[0] + f'\\proposals-{label}.json'
    with open(path, 'w', encoding='utf-8') as f:
        json.dump({'firm': label, 'database': db, 'generated': datetime.datetime.now().isoformat(timespec='seconds'), 'rules': RULES, 'items': items}, f, ensure_ascii=False)
    return path


if __name__ == '__main__':
    # propose.py <export folder> <review.xlsx> [filled-in review.xlsx whose answers to carry over]
    ANSWERS = read_answers(sys.argv[3]) if len(sys.argv) > 3 else {}
    for s in build([('ISTMBM26', 'MBM'), ('ISTMP26', 'MP')]): print(s)
