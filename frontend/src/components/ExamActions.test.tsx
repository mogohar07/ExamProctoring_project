import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { tokenStore } from '../auth/tokenStore';
import { exam, jsonResponse, problem, tokenResponse } from '../test/fixtures';
import type { Exam } from '../types/api';
import { ExamActions } from './ExamActions';

function renderActions(current: Exam) {
  const handlers = { onChanged: vi.fn(), onDeleted: vi.fn(), onStale: vi.fn() };
  render(
    <MemoryRouter>
      <ExamActions exam={current} {...handlers} />
    </MemoryRouter>,
  );
  return handlers;
}

describe('ExamActions', () => {
  it('shows the backend explanation when publishing is refused, and reloads the exam', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(problem(409, 'exam_not_publishable', 'The exam has no questions.')));
    const user = userEvent.setup();
    const handlers = renderActions(exam());

    await user.click(screen.getByRole('button', { name: 'Publish' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Publish' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The exam has no questions.');
    expect(handlers.onChanged).not.toHaveBeenCalled();
    expect(handlers.onStale).toHaveBeenCalledTimes(1);
  });

  it('uses the exam returned by the server after a successful publish', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    const published = exam({ status: 'Published', publishedAt: '2029-12-01T08:00:00+00:00' });
    const fetchMock = vi.fn().mockResolvedValueOnce(jsonResponse(200, published));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const handlers = renderActions(exam());

    await user.click(screen.getByRole('button', { name: 'Publish' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Publish' }));

    expect(await screen.findByText('The exam is published.')).toBeInTheDocument();
    expect(handlers.onChanged).toHaveBeenCalledWith(published);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/exams/e1/publish');
    expect((fetchMock.mock.calls[0][1] as RequestInit).method).toBe('POST');
  });

  it('asks for confirmation before cancelling and does nothing when the instructor keeps the exam', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    renderActions(exam({ status: 'Published' }));

    await user.click(screen.getByRole('button', { name: 'Cancel exam' }));
    expect(screen.getByRole('dialog')).toHaveTextContent('cannot be undone');
    await user.click(screen.getByRole('button', { name: 'Keep' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('offers no lifecycle actions for a cancelled exam', () => {
    renderActions(exam({ status: 'Cancelled', publishedAt: '2029-12-01T08:00:00+00:00' }));

    expect(screen.queryByRole('button', { name: 'Publish' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Unpublish' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel exam' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Edit details' })).not.toBeInTheDocument();
  });

  it('disables publishing once the exam has started', () => {
    renderActions(exam({ phase: 'Open' }));

    expect(screen.getByRole('button', { name: 'Publish' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Delete' })).toBeEnabled();
  });
});
