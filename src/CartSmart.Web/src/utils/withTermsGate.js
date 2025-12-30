// Wrap an action to require Terms acceptance before executing.
// Usage:
// const { requestAcceptance } = useTermsConsent();
// const onPostClick = () => requestAcceptance(() => actuallyPost());

export function gateWithTerms(requestAcceptance, callback) {
  return (...args) => requestAcceptance(() => callback(...args));
}
