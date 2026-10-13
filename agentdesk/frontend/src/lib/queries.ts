import { useQuery } from '@tanstack/react-query';
import { api } from './api';
import type { KbQuery, TicketQuery } from './types';

export const keys = {
  ticketLists: ['tickets', 'list'] as const,
  ticketList: (q: TicketQuery) => ['tickets', 'list', q] as const,
  ticket: (id: string) => ['tickets', 'detail', id] as const,
  timeline: (id: string) => ['tickets', 'timeline', id] as const,
  attachments: (id: string) => ['tickets', 'attachments', id] as const,
  kbLists: ['kb', 'list'] as const,
  kbList: (q: KbQuery) => ['kb', 'list', q] as const,
  kbArticle: (id: string) => ['kb', 'detail', id] as const,
  kbVersions: (id: string) => ['kb', 'versions', id] as const,
  vocabulary: ['meta', 'vocabulary'] as const,
  config: ['meta', 'config'] as const,
  me: ['meta', 'me'] as const,
  assist: ['meta', 'assist'] as const,
};

export const useVocabulary = () =>
  useQuery({ queryKey: keys.vocabulary, queryFn: api.vocabulary, staleTime: Infinity });

export const useMe = () => useQuery({ queryKey: keys.me, queryFn: api.me, staleTime: 5 * 60_000 });

export const useAssistInfo = () =>
  useQuery({ queryKey: keys.assist, queryFn: api.assistInfo, staleTime: 5 * 60_000 });

export const useTicket = (id: string) => useQuery({ queryKey: keys.ticket(id), queryFn: () => api.getTicket(id) });

export const useTimeline = (id: string) =>
  useQuery({ queryKey: keys.timeline(id), queryFn: () => api.timeline(id) });

export const useAttachments = (id: string) =>
  useQuery({ queryKey: keys.attachments(id), queryFn: () => api.listAttachments(id) });

export const useKbArticle = (id: string | undefined) =>
  useQuery({ queryKey: keys.kbArticle(id ?? ''), queryFn: () => api.getKb(id!), enabled: Boolean(id) });
