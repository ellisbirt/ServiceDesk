import { describe, expect, it } from 'vitest';
import { insertCitation, suggestionToText } from './components/AiPanel';
import { citationText } from './components/Composer';
import { parseTags } from './pages/KbEditorPage';

describe('KB citation helpers', () => {
  it('replaces every placeholder with the citation', () => {
    expect(insertCitation('See {{KB}}. Again: {{KB}}', '{{KB}}', 'KB-1 — Fix VPN')).toBe('See KB-1 — Fix VPN. Again: KB-1 — Fix VPN');
  });

  it('appends the citation when the model forgot the placeholder', () => {
    expect(insertCitation('Try restarting.  \n', '{{KB}}', 'KB-1 — Fix VPN')).toBe('Try restarting.\n\nSee KB-1 — Fix VPN.');
  });

  it('formats citations as number and title', () => {
    expect(citationText({ id: 'kb-1', number: 'KB-1', title: 'Fix VPN' })).toBe('KB-1 — Fix VPN');
  });

  it('turns a suggestion into a numbered markdown list', () => {
    expect(suggestionToText({ title: 'Reset', steps: ['Open settings', 'Click reset'], confidence: 'high', rationale: '' })).toBe(
      '**Reset**\n\n1. Open settings\n2. Click reset',
    );
  });
});

describe('parseTags', () => {
  it('trims, drops empties and de-duplicates', () => {
    expect(parseTags(' vpn, network,,vpn ,  ')).toEqual(['vpn', 'network']);
    expect(parseTags('')).toEqual([]);
  });
});
