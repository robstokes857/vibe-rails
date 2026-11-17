// Strength is a setup/change UI rule only. Login and presence proofs accept legacy PINs.
export function remotePasswordError(value) {
    if (value.length > 128) return 'Use no more than 128 characters.';
    if (Array.from(value).length < 6 || !/\p{L}/u.test(value) || !/\p{N}/u.test(value)) {
        return 'Use at least 6 characters, including a letter and a number.';
    }
    return '';
}
