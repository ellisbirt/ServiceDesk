import { describe, expect, it } from 'vitest';
import { formatBytes, humanize, relativeTime, slaState, statusLabel } from './format';
import type { Sla } from './types';

const NOW = Date.parse('2025-01-01T12:00:00Z');
const at = (minutes: number) => new Date(NOW + minutes * 60_000).toISOString();
const sla = (overrides: Partial<Sla> = {}): Sla => ({
  policyId: 'p',
  firstResponseDueAt: at(120),
  resolutionDueAt: at(24 * 60),
  firstResponseBreached: false,
  resolutionBreached: false,
  ...overrides,
});

describe('slaState', () => {
  it('has nothing to say without an SLA', () => {
    expect(slaState({ status: 'open' }, NOW)).toEqual({ tone: 'none', label: 'No SLA' });
  });

  it('tracks first response until the ticket has been answered', () => {
    expect(slaState({ status: 'new', sla: sla() }, NOW)).toEqual({ tone: 'ok', label: 'Response due in 2 h' });
    expect(slaState({ status: 'new', sla: sla({ firstResponseDueAt: at(30) }) }, NOW)).toEqual({
      tone: 'warn',
      label: 'Response due in 30 min',
    });
    expect(slaState({ status: 'new', sla: sla({ firstResponseDueAt: at(-5) }) }, NOW).label).toBe('Response overdue');
  });

  it('switches to the resolution clock once responded', () => {
    const responded = sla({ firstRespondedAt: at(-60), firstResponseDueAt: at(-30) });
    expect(slaState({ status: 'open', sla: responded }, NOW)).toEqual({ tone: 'ok', label: 'Resolution due in 1 d' });
    expect(slaState({ status: 'open', sla: { ...responded, resolutionBreached: true } }, NOW)).toEqual({
      tone: 'breached',
      label: 'Resolution overdue',
    });
  });

  it('reports the outcome for finished tickets', () => {
    expect(slaState({ status: 'resolved', sla: sla() }, NOW)).toEqual({ tone: 'met', label: 'SLA met' });
    expect(slaState({ status: 'closed', sla: sla({ resolutionBreached: true }) }, NOW)).toEqual({
      tone: 'breached',
      label: 'Resolved late',
    });
  });
});

describe('formatting helpers', () => {
  it('formats byte sizes', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB');
  });

  it('formats relative times', () => {
    expect(relativeTime(at(-0.5), NOW)).toBe('just now');
    expect(relativeTime(at(-5), NOW)).toBe('5 min ago');
    expect(relativeTime(at(180), NOW)).toBe('in 3 h');
    expect(relativeTime(undefined, NOW)).toBe('');
  });

  it('humanizes snake_case vocabulary', () => {
    expect(humanize('service_request')).toBe('Service request');
    expect(statusLabel('on_hold')).toBe('On hold');
    expect(statusLabel('something_new')).toBe('Something new');
  });
});
