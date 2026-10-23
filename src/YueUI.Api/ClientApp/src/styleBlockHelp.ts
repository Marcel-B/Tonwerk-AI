/**
 * One sentence per voice and timbre block, shown as a tooltip: the timbre words are singers' and producers' jargon.
 * The app's `StyleBlocks.explanations` holds the same German sentences; change both together.
 */
const help: Record<string, { de: string; en: string }> = {
  'male vocal': {
    de: 'Ein Mann singt die Hauptstimme.',
    en: 'A man sings the lead.',
  },
  'female vocal': {
    de: 'Eine Frau singt die Hauptstimme.',
    en: 'A woman sings the lead.',
  },
  'male and female duet': {
    de: 'Ein Mann und eine Frau singen im Wechsel oder zusammen.',
    en: 'A man and a woman sing in turns or together.',
  },
  choir: {
    de: 'Viele Stimmen singen gemeinsam, wie ein Chor.',
    en: 'Many voices sing together, like a choir.',
  },
  'child vocal': {
    de: 'Eine Kinderstimme: hoch, hell und unverbraucht.',
    en: "A child's voice: high, bright and fresh.",
  },
  'bright vocal': {
    de: 'Hell und strahlend, mit viel Glanz in den Höhen.',
    en: 'Bright and shining, with a lot of sheen in the highs.',
  },
  'airy vocal': {
    de: 'Luftig und leicht, mit hörbarem Atem um den Ton.',
    en: 'Airy and light, with audible breath around the tone.',
  },
  'warm vocal': {
    de: 'Warm und rund, mit Fülle in der Tiefe und weichen Höhen.',
    en: 'Warm and round, full in the lows with soft highs.',
  },
  'mellow vocal': {
    de: 'Sanft und gedämpft, ohne Schärfe, entspannt gesungen.',
    en: 'Gentle and muted, without edge, sung relaxed.',
  },
  'rich vocal': {
    de: 'Voll und obertonreich, eine Stimme mit viel Substanz.',
    en: 'Full and rich in overtones, a voice with a lot of body.',
  },
  'clear vocal': {
    de: 'Klar und rein, ohne Rauheit oder Hauch, gut verständlich.',
    en: 'Clear and pure, without rasp or breath, easy to understand.',
  },
  'soft vocal': {
    de: 'Leise und zart gesungen, mit wenig Druck.',
    en: 'Sung quietly and tenderly, with little pressure.',
  },
  'sweet vocal': {
    de: 'Lieblich und süß, freundlich und eher hell.',
    en: 'Sweet and lovely, friendly and rather bright.',
  },
  'smooth vocal': {
    de: 'Geschmeidig und glatt, mit fließenden Übergängen zwischen den Tönen.',
    en: 'Silky and even, with flowing moves between the notes.',
  },
  'breathy vocal': {
    de: 'Hauchig: viel Atemluft im Ton, intim und nah.',
    en: 'Breathy: a lot of air in the tone, intimate and close.',
  },
  'whispery vocal': {
    de: 'Fast geflüstert, noch mehr Luft als Ton.',
    en: 'Almost whispered, more air than tone.',
  },
  'powerful vocal': {
    de: 'Kraftvoll und laut, mit viel Stütze und Volumen.',
    en: 'Powerful and loud, with strong support and volume.',
  },
  'soulful vocal': {
    de: 'Gefühlvoll wie im Soul, mit Verzierungen und viel Ausdruck.',
    en: 'Full of feeling as in soul, with runs and lots of expression.',
  },
  'husky vocal': {
    de: 'Belegt und etwas heiser, dunkel und rauchig.',
    en: 'Husky and a little hoarse, dark and smoky.',
  },
  'raspy vocal': {
    de: 'Kratzig: ein reibendes Geräusch liegt auf dem Ton, wie bei Rockstimmen.',
    en: 'Raspy: a scratchy noise rides on the tone, as in rock voices.',
  },
  'gritty vocal': {
    de: 'Körnig und angeraut, mit Biss und etwas Verzerrung.',
    en: 'Grainy and rough, with bite and a little distortion.',
  },
  'raw vocal': {
    de: 'Roh und ungeschliffen, direkt statt poliert.',
    en: 'Raw and unpolished, direct rather than refined.',
  },
  'gravelly vocal': {
    de: 'Wie Schotter: tief, stark angeraut und knarzend.',
    en: 'Like gravel: deep, very rough and creaky.',
  },
  'gruff vocal': {
    de: 'Barsch und brummig, rau und tief.',
    en: 'Gruff and growly, rough and low.',
  },
  'harsh vocal': {
    de: 'Hart und schneidend, bewusst unschön, wie im Metal.',
    en: 'Hard and cutting, deliberately ugly, as in metal.',
  },
  'screamy vocal': {
    de: 'Geschrien statt gesungen, wie im Metalcore oder Punk.',
    en: 'Screamed rather than sung, as in metalcore or punk.',
  },
  'deep vocal': {
    de: 'Tief und dunkel, aus der unteren Stimmlage.',
    en: 'Deep and dark, from the low register.',
  },
  'high-pitched vocal': {
    de: 'Hoch gesungen, aus der oberen Stimmlage.',
    en: 'Sung high, from the upper register.',
  },
  'soprano vocal': {
    de: 'Sopran: die höchste Frauenstimmlage.',
    en: 'Soprano: the highest female voice type.',
  },
  'alto vocal': {
    de: 'Alt: die tiefe Frauenstimmlage.',
    en: 'Alto: the low female voice type.',
  },
  'tenor vocal': {
    de: 'Tenor: die hohe Männerstimmlage.',
    en: 'Tenor: the high male voice type.',
  },
  'baritone vocal': {
    de: 'Bariton: die mittlere Männerstimmlage, zwischen Tenor und Bass.',
    en: 'Baritone: the middle male voice type, between tenor and bass.',
  },
}

export function blockHelp(tag: string, locale: 'de' | 'en'): string | undefined {
  return help[tag.toLowerCase()]?.[locale]
}
