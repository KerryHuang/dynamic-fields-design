import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { computed, toValue, type MaybeRefOrGetter } from 'vue';
import type { McpCreateBody } from 'src/domain/mcp/mcp-types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { SECRET_MUTATION, useSecretMutation } from './secret-mutation';

// AI 連線（MCP）權杖（設計 §13.4）。權杖明文只在建立回應出現一次：用 secret mutation，不進 mutation 快取的 variables，
// 回應（含明文）由呼叫端的 onSuccess 取走後不再存（gcTime 0）。
const MY_KEYS = ['mcp-my-tokens', 'mcp-info', 'mcp-calls'];

export const useMyMcp = () => useQuery({ queryKey: ['mcp-info'], queryFn: dynamicApi.getMyMcp });

export const useMyMcpTokens = (enabled: MaybeRefOrGetter<boolean> = true) =>
  useQuery({ queryKey: ['mcp-my-tokens'], queryFn: dynamicApi.getMyMcpTokens, enabled: computed(() => toValue(enabled)) });

export const useMcpCalls = (id: MaybeRefOrGetter<number | null>) =>
  useQuery({
    queryKey: computed(() => ['mcp-calls', toValue(id)]),
    queryFn: () => dynamicApi.getMcpTokenCalls(toValue(id)!),
    enabled: computed(() => toValue(id) !== null),
  });

export function useCreateMcpToken() {
  const qc = useQueryClient();
  return useSecretMutation((take: (v: { name: string; scopes: string[]; expiresOn: string }) => { password: string }) =>
    useMutation({
      ...SECRET_MUTATION,
      gcTime: 0,
      mutationFn: (v: Omit<McpCreateBody, 'password'>) => dynamicApi.createMcpToken({ ...v, ...take(v) }),
      onSettled: () => Promise.all(MY_KEYS.map((k) => qc.invalidateQueries({ queryKey: [k] }))),
    }));
}

export function useRevokeMyMcpToken() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.revokeMyMcpToken(id),
    onSettled: () => Promise.all([...MY_KEYS, 'mcp-admin-tokens'].map((k) => qc.invalidateQueries({ queryKey: [k] }))),
  });
}

export const useAdminMcpTokens = (filter: MaybeRefOrGetter<{ userId?: string; status?: string }>, enabled: MaybeRefOrGetter<boolean> = true) =>
  useQuery({
    queryKey: computed(() => ['mcp-admin-tokens', toValue(filter)]),
    queryFn: () => dynamicApi.getAdminMcpTokens(toValue(filter)),
    enabled: computed(() => toValue(enabled)),
  });

export function useAdminRevokeMcpToken() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => dynamicApi.adminRevokeMcpToken(id),
    onSettled: () => qc.invalidateQueries({ queryKey: ['mcp-admin-tokens'] }),
  });
}

export function useAdminRevokeAllMcpTokens() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (userId: string) => dynamicApi.adminRevokeAllMcpTokens(userId),
    onSettled: () => qc.invalidateQueries({ queryKey: ['mcp-admin-tokens'] }),
  });
}
