// DTO types matching the gateway API contract (camelCase JSON).

export type Role = 'platform_admin' | 'owner' | 'admin' | 'viewer'

export interface UserSummary {
  id: number
  email: string
  displayName: string
  role: Role
  tenantId: string | null
  tenantName: string | null
}

export interface TokenResponse {
  accessToken: string
  tokenType: string
  expiresIn: number
  refreshToken: string
  refreshExpiresAt: string
  user: UserSummary
}

export interface AdminUser {
  id: number
  email: string
  displayName: string
  role: Role
  isActive: boolean
  lastLoginAt: string | null
  createdAt: string
}

export interface InviteResponse {
  user: AdminUser
  inviteToken: string
}

export interface ProjectDto {
  id: string
  name: string
  status: string
  logContent: boolean
  contentRetentionDays: number | null
  createdAt: string
}

export interface ApiKeyDto {
  id: string
  projectId: string
  name: string
  keyPrefix: string
  allowedIps: string[]
  expiresAt: string | null
  revokedAt: string | null
  lastUsedAt: string | null
  createdAt: string
  plaintextKey?: string
}

export interface PolicyDto {
  scope: string
  scopeId: string
  maxTokensPerRequest: number | null
  requestsPerMinute: number | null
  dailyTokenQuota: number | null
  monthlyTokenQuota: number | null
  monthlyBudget: number | null
  allowedModels: string[] | null
  enabled: boolean
}

export interface UsageRow {
  key: string
  label: string
  requests: number
  denied: number
  errors: number
  inputTokens: number
  outputTokens: number
  cost: number
}

export interface UsageLogItem {
  id: number
  createdAt: string
  requestId: string
  projectId: string | null
  keyId: string | null
  modelId: string | null
  status: string
  httpStatus: number
  deniedReason: string | null
  inputTokens: number
  outputTokens: number
  cachedTokens: number
  reasoningTokens: number
  cost: number
  latencyMs: number
  attempts: number
  fallbackUsed: boolean
  endUser: string | null
}

export interface Page<T> {
  items: T[]
  total: number
  pageNumber: number
  pageSize: number
}

export interface AuditItem {
  id: number
  userId: number | null
  action: string
  entity: string | null
  entityId: string | null
  detailJson: string | null
  ip: string | null
  createdAt: string
}

// Providers & models (existing endpoints).
export interface ProviderDto {
  id: string
  name: string
  type: string
  baseUrl: string
  authHeader: string
  authPrefix: string
  modelsPath: string | null
  enabled: boolean
  templateCode: string | null
  keyHint: string | null
  createdAt: string
}

export interface RouteDto {
  providerId: string
  providerName: string
  upstreamModel: string
  priority: number
  weight: number
  enabled: boolean
}

export interface TierDto {
  minInputTokens: number
  input: number
  output: number
  cacheRead: number | null
  cacheWrite: number | null
}

export interface PriceDto {
  input: number
  output: number
  cacheRead: number | null
  cacheWrite: number | null
  currency: string
  effectiveFrom: string
  tiers: TierDto[]
}

export interface ModelDto {
  id: string
  alias: string
  description: string | null
  maxOutputTokens: number | null
  enabled: boolean
  routes: RouteDto[]
  price: PriceDto | null
  createdAt: string
}

export interface TemplateDto {
  id: number
  code: string
  name: string
  type: string
  defaultBaseUrl: string | null
  authHeader: string | null
  authPrefix: string | null
  modelsPath: string | null
  syncKind: string | null
  syncUrl: string | null
  enabled: boolean
}

export interface CatalogModelDto {
  id: number
  templateCode: string
  upstreamModel: string
  displayName: string
  apiFamily: string | null
  contextWindow: number | null
  maxInputTokens: number | null
  maxOutputTokens: number | null
  inputPricePer1M: number | null
  outputPricePer1M: number | null
  cacheReadPricePer1M: number | null
  cacheWritePricePer1M: number | null
  tiers: unknown
  currency: string | null
  supportsTools: boolean
  supportsVision: boolean
  supportsReasoning: boolean
  source: string
  enabled: boolean
}

export interface PlanDto {
  id: number
  name: string
  maxProjects: number | null
  maxApiKeys: number | null
  maxRequestsPerMonth: number | null
  maxTokensPerMonth: number | null
}

export interface PlatformAuditItem {
  id: number
  userId: number | null
  action: string
  entity: string | null
  entityId: string | null
  ip: string | null
  createdAt: string
}

export interface SyncResult {
  templatesAdded: number
  modelsAdded: number
  modelsUpdated: number
  modelsSkippedManual: number
  modelsUnsupported: number
}

export interface TenantDto {
  id: string
  name: string
  slug: string
  status: string
  planId: number
  planName: string
  createdAt: string
}

export interface TenantWithInvite {
  tenant: TenantDto
  inviteToken: string
}

export const ROLES: Role[] = ['owner', 'admin', 'viewer']
export const PROJECT_STATUSES = ['active', 'suspended']
export const TENANT_STATUSES = ['active', 'suspended']

// G4 maintenance: alerts, webhooks, privacy body viewer.
export interface AlertRuleDto {
  id: string
  name: string
  metric: string
  scope: string
  scopeId: string | null
  thresholdPercent: number
  webhookId: string | null
  enabled: boolean
  createdAt: string
  lastTriggeredAt: string | null
}

export interface AlertEventDto {
  ruleId: string
  ruleName: string
  metric: string
  scope: string
  thresholdPercent: number
  periodStart: string
  observed: number
  limit: number
  createdAt: string
}

export interface WebhookDto {
  id: string
  name: string
  url: string
  secretHint: string
  enabled: boolean
  createdAt: string
  updatedAt: string
}

export interface DeliveryDto {
  id: string
  status: string
  attempts: number
  nextAttemptAt: string | null
  responseStatus: number | null
  lastError: string | null
  createdAt: string
  deliveredAt: string | null
}

export interface UsageBodyDto {
  usageLogId: number
  requestJson: string | null
  responseJson: string | null
  expiresAt: string | null
}

export const ALERT_METRICS = ['daily_tokens', 'monthly_tokens', 'monthly_budget']
export const ALERT_SCOPES = ['tenant', 'project', 'key']
