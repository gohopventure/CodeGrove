import { PageTitle } from '../components/ui';

/**
 * Plain-language summary for families. PLACEHOLDER: the legal privacy policy and COPPA online
 * notice must be written with counsel before launch; this page links to it when it exists.
 */
export function PrivacyPage() {
  return (
    <div className="prose-lesson mx-auto">
      <PageTitle>Privacy for families</PageTitle>
      <h2>The short version</h2>
      <ul>
        <li>Children under 13 can only join through a parent's account, after the parent gives consent.</li>
        <li>We collect a username, password, birth month and year, and learning progress. Nothing else.</li>
        <li>No ads, no advertising trackers, no selling or sharing data.</li>
        <li>No public profiles and no messaging between users.</li>
        <li>Parents can see, change or delete their child's data at any time from the family dashboard.</li>
      </ul>
      <h2>How long we keep things</h2>
      <p>Code submissions: 12 months. Accounts unused for 18 months are deleted after we email the parent. Unconfirmed child accounts are deleted after 14 days.</p>
      <p className="text-muted">The full privacy policy will be published here before launch.</p>
    </div>
  );
}
