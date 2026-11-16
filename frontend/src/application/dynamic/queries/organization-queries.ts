import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import type { DepartmentInput } from 'src/domain/dynamic/organization';
import { dynamicApi } from 'src/services/api/dynamic-api';

export function useDepartments() {
  return useQuery({ queryKey: ['departments'], queryFn: dynamicApi.getDepartments });
}

/** Departments carry member ids and admin users carry departmentId, so both change together. */
function useOrgMutation<V>(fn: (v: V) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => Promise.all(['departments', 'admin-users'].map((k) => qc.invalidateQueries({ queryKey: [k] }))),
  });
}

export const useCreateDepartment = () => useOrgMutation((body: DepartmentInput) => dynamicApi.createDepartment(body));
export const useUpdateDepartment = () =>
  useOrgMutation((v: { id: number; body: DepartmentInput }) => dynamicApi.updateDepartment(v.id, v.body));
export const useDeleteDepartment = () => useOrgMutation((id: number) => dynamicApi.deleteDepartment(id));
export const useSetUserDepartment = () =>
  useOrgMutation((v: { userId: string; departmentId: number | null }) => dynamicApi.setUserDepartment(v.userId, v.departmentId));
