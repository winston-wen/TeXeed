// Keep the original decimal digits (including full-width digits and leading zeros).
globalThis.isReadingNumber = tex => /^\p{Decimal_Number}+$/u.test(tex.trim());
