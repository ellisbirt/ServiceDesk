// Mirrors the AgentDesk API contract (camelCase JSON, nulls omitted). See agentdesk/backend/src/AgentDesk.Core/Domain.

export type TicketStatus = 'new' | 'open' | 'pending' | 'on_hold' | 'resolved' | 'closed' | 'cancelled';
export type TicketType = 'incident' | 'service_request' | 'question' | 'problem';
export type Priority = 'P1' | 'P2' | 'P3' | 'P4';
export type Visibility = 'public' | 'internal';
export type KbStatus = 'draft' | 'published' | 'archived';
export type EmbeddingStatus = 'pending' | 'ready' | 'failed' | 'disabled';
export type ImproveMode = 'polish' | 'shorten' | 'friendlier' | 'formal' | 'expand';

export interface Person {
  id: string;
  displayName: string;
  email?: string;
}

export interface AiAssisted {
  feature: string;
  provider: string;
  model: string;
  promptVersion: string;
}

export interface Sla {
  policyId: string;
  firstResponseDueAt: string;
  resolutionDueAt: string;
  firstRespondedAt?: string;
  firstResponseBreached: boolean;
  resolutionBreached: boolean;
}

export interface Resolution {
  code: string;
  notes?: string;
  resolvedAt: string;
  resolvedBy: Person;
}

export interface LinkedTicket {
  ticketId: string;
  number: string;
  relation: string;
}

export interface Ticket {
  id: string;
  tenantId: string;
  createdAt: string;
  createdBy?: Person;
  updatedAt: string;
  updatedBy?: Person;
  _etag?: string;
  number: string;
  subject: string;
  description: string;
  /** Summary of the requester's chat, present on tickets raised from the intake chat. */
  conversationSummary?: string;
  type: TicketType;
  status: TicketStatus;
  pendingReason?: string;
  impact: number;
  urgency: number;
  priority: Priority;
  category?: string;
  subcategory?: string;
  tags?: string[];
  requester: Person;
  assignmentGroup?: string;
  assignee?: Person;
  watchers?: Person[];
  channel: string;
  source?: { eventId?: string; conversationId?: string; correlationId?: string };
  linkedTickets?: LinkedTicket[];
  kbArticleIds?: string[];
  sla?: Sla;
  resolution?: Resolution;
  attachmentCount?: number;
  lastActivityAt?: string;
}

export interface CitedKbArticle {
  id: string;
  number: string;
  version: number;
  title: string;
}

export interface FieldChange {
  field: string;
  from?: string;
  to?: string;
}

export interface TimelineEntry {
  id: string;
  docType: 'comment' | 'event';
  ticketId: string;
  author: Person;
  createdAt: string;
  visibility?: Visibility;
  body?: string;
  attachmentIds?: string[];
  citedKbArticles?: CitedKbArticle[];
  aiAssisted?: AiAssisted;
  eventType?: string;
  changes?: FieldChange[];
  note?: string;
}

export interface Page<T> {
  items: T[];
  continuationToken?: string;
}

export interface Attachment {
  id: string;
  ticketId: string;
  commentId?: string;
  fileName: string;
  contentType: string;
  size: number;
  sha256?: string;
  status: 'pending_upload' | 'available' | 'quarantined' | 'rejected';
  scanResult?: string;
  createdAt: string;
  createdBy?: Person;
}

export interface CreateAttachmentResult {
  attachment: Attachment;
  uploadUrl: string;
  expiresAt: string;
  headers: Record<string, string>;
}

export interface KbArticleInput {
  title: string;
  summary: string;
  body: string;
  symptoms?: string;
  cause?: string;
  resolution?: string;
  category?: string;
  tags?: string[];
  visibility?: Visibility;
  sourceTicketIds?: string[];
  aiAssisted?: AiAssisted;
}

export interface EmbeddingInfo {
  status: EmbeddingStatus;
  provider?: string;
  model?: string;
  dimensions?: number;
  embeddedAt?: string;
  error?: string;
  attempts?: number;
  hasVector?: boolean;
}

export interface KbArticle {
  id: string;
  number: string;
  title: string;
  summary: string;
  body: string;
  symptoms?: string;
  cause?: string;
  resolution?: string;
  category?: string;
  tags?: string[];
  status: KbStatus;
  visibility: Visibility;
  version: number;
  sourceTicketIds?: string[];
  author?: Person;
  createdAt: string;
  createdBy?: Person;
  updatedAt: string;
  updatedBy?: Person;
  publishedAt?: string;
  publishedBy?: Person;
  archivedAt?: string;
  reviewDueAt?: string;
  aiAssisted?: AiAssisted;
  embedding?: EmbeddingInfo;
  eTag?: string;
}

export interface KbVersion {
  articleId: string;
  version: number;
  status: KbStatus;
  title: string;
  summary: string;
  body: string;
  symptoms?: string;
  cause?: string;
  resolution?: string;
  category?: string;
  tags?: string[];
  createdAt?: string;
  createdBy?: Person;
}

export interface Vocabulary {
  ticketStatuses: TicketStatus[];
  activeStatuses: TicketStatus[];
  transitions: Record<string, TicketStatus[]>;
  pendingReasons: string[];
  ticketTypes: TicketType[];
  channels: string[];
  priorities: Priority[];
  categories: string[];
  resolutionCodes: string[];
  visibilities: Visibility[];
  kbStatuses: KbStatus[];
  impactLevels: number[];
  urgencyLevels: number[];
}

export interface ClientConfig {
  auth: {
    mode: string;
    hubTicketRequired: boolean;
    authority?: string;
    clientId?: string;
    apiScope?: string;
  };
}

export interface Me {
  tenantId: string;
  userId: string;
  displayName?: string;
  email?: string;
  roles: string[];
}

export interface AssistInfo {
  available: boolean;
  provider?: string;
  model?: string;
  offline?: boolean;
  features?: string[];
  improveModes?: ImproveMode[];
  kbPlaceholder?: string;
}

export interface Suggestion {
  title: string;
  steps: string[];
  confidence: string;
  rationale: string;
}

export interface SuggestSolutionsResult {
  summary: string;
  suggestions: Suggestion[];
  clarifyingQuestions: string[];
  aiAssisted: AiAssisted;
}

export interface DraftReplyResult {
  body: string;
  aiAssisted: AiAssisted;
}

export interface DraftKbResult {
  article: KbArticleInput;
  aiAssisted: AiAssisted;
}

export interface ReplyWithKbResult {
  article: KbArticleInput;
  replyBody: string;
  placeholder: string;
  aiAssisted: AiAssisted;
}

export interface ImproveResult {
  text: string;
  aiAssisted: AiAssisted;
}

export interface CreateTicketInput {
  subject: string;
  description: string;
  type?: TicketType;
  impact?: number;
  urgency?: number;
  category?: string;
  subcategory?: string;
  tags?: string[];
  requester?: Person;
  assignmentGroup?: string;
  assignee?: Person;
  channel?: string;
}

export interface UpdateTicketInput {
  subject?: string;
  description?: string;
  type?: TicketType;
  impact?: number;
  urgency?: number;
  category?: string;
  subcategory?: string;
  tags?: string[];
  watchers?: Person[];
}

export interface TransitionInput {
  status: TicketStatus;
  pendingReason?: string;
  resolutionCode?: string;
  notes?: string;
}

export interface AddCommentInput {
  visibility: Visibility;
  body: string;
  attachmentIds?: string[];
  citedKbArticleIds?: string[];
  aiAssisted?: AiAssisted;
  transition?: TransitionInput;
}

export interface AddCommentResult {
  ticket: Ticket;
  comment: TimelineEntry;
  publishedArticles?: KbArticle[];
}

export interface TicketQuery {
  status?: string;
  priority?: string;
  assignee?: string;
  unassigned?: boolean;
  group?: string;
  category?: string;
  q?: string;
  breached?: boolean;
  sort?: string;
  pageSize?: number;
  continuationToken?: string;
}

export interface KbQuery {
  status?: string;
  category?: string;
  q?: string;
  pageSize?: number;
  continuationToken?: string;
}
