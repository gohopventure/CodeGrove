// Mirrors the API's response records. Kept by hand for the MVP; generate from OpenAPI once the
// API surface settles (tradeoff: a small amount of duplication now vs. a codegen step in the build).
import type { TestResult, TestSpec } from '../runner/types';

export type AgeBand = 'Explorer' | 'Builder' | 'Adult' | 'Both';
export type AccountStatus = 'PendingConsent' | 'Active' | 'Suspended';
export type ConsentStatus = 'Pending' | 'Granted' | 'Revoked' | 'Expired';
export type Role = 'Learner' | 'Parent' | 'Teacher' | 'Admin';
export type Files = Record<string, string>;

export interface Me {
  id: string;
  userName: string;
  roles: Role[];
  ageBand: AgeBand;
  status: AccountStatus;
  useDyslexiaFont: boolean;
  reducedMotion: boolean;
}

export interface TrackSummary {
  slug: string;
  title: string;
  description: string;
  ageBand: AgeBand;
  courseCount: number;
  prerequisites: string[];
}

export interface CourseSummary {
  slug: string;
  title: string;
  summary: string;
  estimatedMinutes: number;
  isPublished: boolean;
  lessonCount: number;
  lessonsCompleted: number;
}

export interface TrackDetail {
  slug: string;
  title: string;
  description: string;
  ageBand: AgeBand;
  prerequisites: TrackSummary[];
  courses: CourseSummary[];
  certificateCode: string | null;
}

export interface LessonSummary {
  id: number;
  slug: string;
  title: string;
  estimatedMinutes: number;
  isPublished: boolean;
  completed: boolean;
}

export interface CourseDetail {
  slug: string;
  title: string;
  summary: string;
  estimatedMinutes: number;
  isPublished: boolean;
  objectives: string[];
  lessons: LessonSummary[];
}

export type TutorMood = 'Neutral' | 'Encouraging' | 'Thinking' | 'Celebrating';

/** One thing the tutor says: `displayText` for the screen (may have `code`), `speechText` for a voice later. */
export interface TutorLine {
  displayText: string;
  speechText: string;
  mood: TutorMood;
}

export type TutorEvent = 'firstFail' | 'stuckOffer' | 'pass';

export interface Tutor {
  /** The character for the learner's band. `portraitUrl` has a `{mood}` placeholder. Null if none is set up. */
  character: { slug: string; name: string; portraitUrl: string } | null;
  intro: TutorLine[];
  recap: TutorLine[];
  /** Reusable lines by event; the player picks one at random. */
  lines: Partial<Record<TutorEvent, TutorLine[]>>;
}

export interface RevealedHint {
  level: number;
  markdown: string;
  speechText: string;
  mood: TutorMood;
}

export interface Challenge {
  id: number;
  kind: 'Html' | 'Css' | 'Js' | 'React' | 'TypeScript' | 'Angular' | 'Sql' | 'CSharp' | 'FullStack';
  instructionsMarkdown: string;
  starterFiles: Files;
  tests: TestSpec[];
  hintLevels: number[];
  revealedHints: RevealedHint[];
  /** Only after the learner asked for it (after all three hints) or passed. */
  solutionFiles: Files | null;
  xpReward: number;
  /** Present when the challenge can be built with blocks (Explorer JavaScript lessons). */
  blocks: { toolbox: string[]; starter: object | null } | null;
  /**
   * Capstone milestones: the starter with the learner's own passing code from the previous milestone swapped in,
   * and that milestone's title. Null when the challenge starts fresh or they have no passing code there yet.
   */
  startingFiles?: Files | null;
  carriedFrom?: string | null;
}

export interface QuizQuestion {
  id: number;
  prompt: string;
  options: { id: number; text: string }[];
}

export interface Quiz {
  id: number;
  passPercent: number;
  questions: QuizQuestion[];
}

export interface Lesson {
  id: number;
  slug: string;
  title: string;
  courseSlug: string;
  courseTitle: string;
  ageBand: AgeBand;
  estimatedMinutes: number;
  learnMarkdown: string;
  videoUrl: string | null;
  captionsUrl: string | null;
  exampleMarkdown: string;
  exampleCode: string | null;
  challenge: Challenge | null;
  quiz: Quiz | null;
  progress: { learnViewed: boolean; challengePassed: boolean; quizPassed: boolean; completed: boolean };
  previousLessonId: number | null;
  nextLessonId: number | null;
  tutor: Tutor;
}

export interface AwardedBadge {
  code: string;
  name: string;
  description: string;
  iconUrl: string;
}

export interface ProgressOutcome {
  xpAwarded: number;
  totalXp: number;
  level: number;
  newBadges: AwardedBadge[];
  lessonCompleted: boolean;
  courseCompleted: boolean;
  certificateCode: string | null;
  /** Set when this pass put the app in the learner's portfolio (or updated it): the capstone's last milestone. */
  portfolioEntryId?: string | null;
}

/** A finished project in the portfolio. Private: only the learner and their parent can see it. */
export interface PortfolioEntry {
  id: string;
  title: string;
  /** The capstone theme it was built in, e.g. "Grove Club Hub". */
  theme: string;
  courseTitle: string;
  lessonId: number;
  lessonTitle: string;
  createdAt: string;
  updatedAt: string;
  /** Null until the learner's browser has sent a picture of the page. */
  screenshotUrl: string | null;
}

export interface PortfolioFiles {
  title: string;
  files: Files;
}

export interface SubmissionResponse {
  passed: boolean;
  outcome: ProgressOutcome;
}

export type HintResponse = RevealedHint;

export interface SolutionResponse {
  solutionFiles: Files;
}

export interface QuizAttemptResponse {
  scorePercent: number;
  passed: boolean;
  passPercent: number;
  results: { questionId: number; correct: boolean; explanation: string }[];
  outcome: ProgressOutcome;
}

export interface HeartbeatResult {
  todaySeconds: number;
  dailyMinutesLimit: number | null;
  limitReached: boolean;
}

export interface Badge extends AwardedBadge {
  earned: boolean;
  awardedAt: string | null;
}

export interface Dashboard {
  userName: string;
  ageBand: AgeBand;
  xp: number;
  level: number;
  xpForNextLevel: number;
  streakWeeks: number;
  weeklyGoalDays: number;
  daysActiveThisWeek: number;
  todaySeconds: number;
  dailyMinutesLimit: number | null;
  lessonsCompleted: number;
  badges: Badge[];
  certificates: { code: string; trackTitle: string; issuedAt: string }[];
  continue: { lessonId: number; lessonTitle: string; courseTitle: string; courseSlug: string } | null;
}

export interface ChildSummary {
  id: string;
  userName: string;
  ageBand: AgeBand;
  status: AccountStatus;
  consentStatus: ConsentStatus | null;
  xp: number;
  level: number;
  lessonsCompleted: number;
  minutesLast7Days: number;
  lastActiveAt: string | null;
}

export interface ChildSettings {
  dailyMinutesLimit: number | null;
  weeklyGoalDays: number;
  weeklyDigest: boolean;
  allowAiTutor: boolean;
  allowCommunity: boolean;
}

export interface ChildProgress {
  child: ChildSummary;
  settings: ChildSettings;
  courses: {
    slug: string;
    title: string;
    lessonsTotal: number;
    lessonsCompleted: number;
    lessons: { lessonId: number; title: string; challengePassed: boolean; bestQuizScore: number | null; hintsUsed: number; completedAt: string | null }[];
  }[];
  last14Days: { date: string; minutes: number; lessonsCompleted: number }[];
  badges: string[];
}

export interface CreateChildResponse {
  child: ChildSummary;
  consentRequired: boolean;
  devConfirmUrl: string | null;
}

export interface Certificate {
  code: string;
  userName: string;
  trackTitle: string;
  issuedAt: string;
}

export type { TestResult, TestSpec };
