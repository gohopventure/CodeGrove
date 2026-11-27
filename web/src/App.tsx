import { lazy, Suspense } from 'react';
import { Route, Routes } from 'react-router-dom';
import { Layout } from './components/Layout';
import { RequireRole } from './components/RequireRole';
import { Spinner } from './components/ui';
import { CertificatePage } from './pages/CertificatePage';
import { ConsentConfirmPage } from './pages/ConsentConfirmPage';
import { CoursePage } from './pages/CoursePage';
import { LandingPage } from './pages/LandingPage';
import { LearnHomePage } from './pages/LearnHomePage';
import { LoginPage } from './pages/LoginPage';
import { BadgesPage, CertificatesPage, PortfolioPage } from './pages/MePages';
import { NotFoundPage } from './pages/NotFoundPage';
import { ParentChildPage } from './pages/ParentChildPage';
import { ParentHomePage } from './pages/ParentHomePage';
import { PrivacyPage } from './pages/PrivacyPage';
import { SettingsPage } from './pages/SettingsPage';
import { SignupChoicePage, SignupParentPage, SignupTeenPage } from './pages/SignupPages';
import { TrackPage, TracksPage } from './pages/TracksPages';

// The lesson player pulls in Monaco (several MB), so it loads only when a lesson is opened.
const LessonPage = lazy(() => import('./lesson/LessonPage'));

export function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<LandingPage />} />
        <Route path="login" element={<LoginPage />} />
        <Route path="signup" element={<SignupChoicePage />} />
        <Route path="signup/parent" element={<SignupParentPage />} />
        <Route path="signup/teen" element={<SignupTeenPage />} />
        <Route path="tracks" element={<TracksPage />} />
        <Route path="tracks/:slug" element={<TrackPage />} />
        <Route path="courses/:slug" element={<CoursePage />} />
        <Route path="certificates/:code" element={<CertificatePage />} />
        <Route path="privacy" element={<PrivacyPage />} />

        <Route element={<RequireRole role="Learner" />}>
          <Route path="learn" element={<LearnHomePage />} />
          <Route
            path="lessons/:lessonId"
            element={
              <Suspense fallback={<Spinner label="Loading your lesson" />}>
                <LessonPage />
              </Suspense>
            }
          />
          <Route path="me/badges" element={<BadgesPage />} />
          <Route path="me/certificates" element={<CertificatesPage />} />
          <Route path="me/portfolio" element={<PortfolioPage />} />
        </Route>

        <Route element={<RequireRole role="Parent" />}>
          <Route path="parent" element={<ParentHomePage />} />
          <Route path="parent/children/:childId" element={<ParentChildPage />} />
          <Route path="consent/confirm" element={<ConsentConfirmPage />} />
        </Route>

        <Route element={<RequireRole />}>
          <Route path="settings" element={<SettingsPage />} />
        </Route>

        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
