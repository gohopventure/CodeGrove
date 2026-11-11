import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, localDate } from './client';
import type {
  Certificate, ChildProgress, ChildSettings, ChildSummary, CourseDetail, CreateChildResponse, Dashboard,
  Badge, Lesson, Me, TrackDetail, TrackSummary,
} from './types';

/** Query keys in one place so invalidation stays consistent. */
export const keys = {
  me: ['me'] as const,
  tracks: (band?: string) => ['tracks', band ?? 'all'] as const,
  track: (slug: string) => ['track', slug] as const,
  course: (slug: string) => ['course', slug] as const,
  lesson: (id: number) => ['lesson', id] as const,
  dashboard: ['dashboard'] as const,
  badges: ['badges'] as const,
  children: ['children'] as const,
  child: (id: string) => ['child', id] as const,
  certificate: (code: string) => ['certificate', code] as const,
};

export function useMe() {
  return useQuery({
    queryKey: keys.me,
    // 204 (no body) means signed out.
    queryFn: async () => (await api.get<Me | undefined>('/api/auth/me')) ?? null,
    staleTime: 5 * 60_000,
  });
}

export const useTracks = (band?: string) =>
  useQuery({ queryKey: keys.tracks(band), queryFn: () => api.get<TrackSummary[]>(`/api/tracks${band ? `?band=${band}` : ''}`) });

export const useTrack = (slug: string) =>
  useQuery({ queryKey: keys.track(slug), queryFn: () => api.get<TrackDetail>(`/api/tracks/${encodeURIComponent(slug)}`) });

export const useCourse = (slug: string) =>
  useQuery({ queryKey: keys.course(slug), queryFn: () => api.get<CourseDetail>(`/api/courses/${encodeURIComponent(slug)}`) });

export const useLesson = (id: number) =>
  useQuery({ queryKey: keys.lesson(id), queryFn: () => api.get<Lesson>(`/api/lessons/${id}`), enabled: Number.isFinite(id) });

export const useDashboard = () =>
  useQuery({ queryKey: keys.dashboard, queryFn: () => api.get<Dashboard>(`/api/me/dashboard?localDate=${localDate()}`) });

export const useBadges = () => useQuery({ queryKey: keys.badges, queryFn: () => api.get<Badge[]>('/api/me/badges') });

export const useChildren = () =>
  useQuery({ queryKey: keys.children, queryFn: () => api.get<ChildSummary[]>('/api/parent/children') });

export const useChildProgress = (id: string) =>
  useQuery({
    queryKey: keys.child(id),
    queryFn: () => api.get<ChildProgress>(`/api/parent/children/${id}/progress?localDate=${localDate()}`),
  });

export const useCertificate = (code: string) =>
  useQuery({ queryKey: keys.certificate(code), queryFn: () => api.get<Certificate>(`/api/certificates/${encodeURIComponent(code)}`), retry: false });

// ---- Mutations ----------------------------------------------------------------------------------

export function useLogin() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { login: string; password: string }) => api.post<Me>('/api/auth/login', body),
    onSuccess: (me) => {
      qc.clear(); // drop anything cached for a previous user on this device
      qc.setQueryData(keys.me, me);
    },
  });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post('/api/auth/logout'),
    onSettled: () => {
      qc.clear();
      qc.setQueryData(keys.me, null);
    },
  });
}

export function useRegister(kind: 'parent' | 'teen') {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: Record<string, unknown>) => api.post<Me>(`/api/auth/register-${kind}`, body),
    onSuccess: (me) => {
      qc.clear();
      qc.setQueryData(keys.me, me);
    },
  });
}

export function useCreateChild() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { userName: string; password: string; birthYear: number; birthMonth: number; ageBand?: string }) =>
      api.post<CreateChildResponse>('/api/parent/children', body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.children }),
  });
}

export function useChildSettings(childId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: ChildSettings) => api.put(`/api/parent/children/${childId}/settings`, s),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.child(childId) }),
  });
}

export function usePreferences() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (p: { useDyslexiaFont: boolean; reducedMotion: boolean }) => api.put('/api/me/preferences', p),
    onMutate: (p) => qc.setQueryData<Me | null>(keys.me, (me) => (me ? { ...me, ...p } : me)),
  });
}
