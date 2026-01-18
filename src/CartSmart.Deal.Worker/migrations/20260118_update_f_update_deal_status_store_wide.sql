-- Update f_update_deal_status to support store-wide deals.
--
-- Backward compatible:
-- - Existing callers can keep calling f_update_deal_status(p_deal_product_id)
-- - Store-wide callers can call f_update_deal_status(NULL, p_deal_id)
--
-- Notes:
-- - Store-wide deals have no deal_product rows, so we compute status directly on deal.id


CREATE OR REPLACE FUNCTION public.f_update_deal_status(
  p_deal_product_id int DEFAULT NULL,
  p_deal_id int DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
  v_new_dp_status int;
  v_old_dp_status int;
  v_deal_id int;
  v_product_id int;
  v_submitter_id int;
  v_issue_status boolean;
  v_primary boolean;
  v_flagger_id int;
  v_parent_deal_status int;
  -- Penalty constants (keep in sync with C#)
  c_penalty_removed_submission int := 12;
  c_penalty_false_flag int := 6;
BEGIN
  -----------------------------------------------------------------------------
  -- Store-wide path: p_deal_product_id is NULL; compute directly on deal id.
  -----------------------------------------------------------------------------
  IF p_deal_product_id IS NULL THEN
    v_deal_id := p_deal_id;

    IF v_deal_id IS NULL THEN
      RETURN NULL;
    END IF;

    SELECT d.deal_status_id, d.user_id
      INTO v_old_dp_status, v_submitter_id
    FROM deal d
    WHERE d.id = v_deal_id;

    IF v_submitter_id IS NULL THEN
      RETURN NULL;
    END IF;

    -- Decide new status (admin override > majority >=2)
    WITH reviews AS (
      SELECT dr.deal_status_id, u.admin, dr.created_at
        FROM deal_review dr
        JOIN "user" u ON u.id = dr.user_id
       WHERE dr.deal_id = v_deal_id
         AND dr.deal_product_id IS NULL
       ORDER BY dr.created_at DESC
    ),
    admin_latest AS (
      SELECT deal_status_id FROM reviews WHERE admin IS TRUE ORDER BY created_at DESC LIMIT 1
    ),
    rev_count AS (
      SELECT count(*) AS cnt FROM reviews
    ),
    majority AS (
      SELECT deal_status_id
        FROM reviews
       GROUP BY deal_status_id
       ORDER BY count(*) DESC, deal_status_id
       LIMIT 1
    )
    SELECT COALESCE(
             (SELECT deal_status_id FROM admin_latest),
             CASE WHEN (SELECT cnt FROM rev_count) >= 2
                  THEN (SELECT deal_status_id FROM majority)
             END
           )
      INTO v_new_dp_status;

    IF v_new_dp_status IS NOT NULL AND v_new_dp_status <> v_old_dp_status THEN
      UPDATE deal
         SET deal_status_id = v_new_dp_status
       WHERE id = v_deal_id
       RETURNING deal_status_id INTO v_parent_deal_status;

      -- Notify user on approve/reject (mirrors product-deal behavior)
      IF v_new_dp_status = 2 THEN
        INSERT INTO notifications(user_id, type_id, message, link_url)
        VALUES (v_submitter_id, 1, 'Your deal was approved!', '/profile?dealId=' || v_deal_id::text);
      ELSIF v_new_dp_status = 3 THEN
        INSERT INTO notifications(user_id, type_id, message, link_url)
        VALUES (v_submitter_id, 2, 'Your deal was rejected.', '/profile?dealId=' || v_deal_id::text);
      END IF;
    ELSE
      v_parent_deal_status := v_old_dp_status;
    END IF;

    -- Remove any stacked deals that use this if it's not active anymore
    IF v_parent_deal_status IS NOT NULL
       AND v_parent_deal_status IN (3,4,6,7) THEN
      UPDATE deal d_main
         SET deal_status_id = 4
       WHERE d_main.id IN (
         SELECT dc.deal_id
           FROM deal_combo dc
          WHERE dc.combo_deal_id = v_deal_id
       );
    END IF;

    -- Reputation adjustments (store-wide deals)
    -- Run only on actual status change.
    v_primary := true;
    IF v_new_dp_status IS NOT NULL AND v_new_dp_status <> v_old_dp_status AND v_primary THEN
      v_issue_status := v_new_dp_status IN (3,5);  -- 3 Removed, 5 Review Again

      -- Ensure submitter row
      INSERT INTO user_reputation (user_id, submit_total, submit_correct, flag_total, flag_true, consecutive_false_flags, penalty_points, updated_at, trust_score)
      VALUES (v_submitter_id,0,0,0,0,0,0, now(), 0)
      ON CONFLICT (user_id) DO NOTHING;

      -- Submit total (one-time)
      IF NOT EXISTS (
        SELECT 1 FROM user_reputation_event
         WHERE user_id = v_submitter_id
           AND deal_id = v_deal_id
           AND deal_product_id IS NULL
           AND event_type = 'submit'
      ) THEN
        UPDATE user_reputation
           SET submit_total = submit_total + 1,
               updated_at = now()
         WHERE user_id = v_submitter_id;

        INSERT INTO user_reputation_event(user_id, deal_id, event_type)
        VALUES (v_submitter_id, v_deal_id, 'submit');
      END IF;

      -- Submit correct (only if approved)
      IF v_new_dp_status = 2
         AND NOT EXISTS (
            SELECT 1 FROM user_reputation_event
             WHERE user_id = v_submitter_id
               AND deal_id = v_deal_id
               AND deal_product_id IS NULL
               AND event_type = 'submit_correct'
         ) THEN
        UPDATE user_reputation
           SET submit_correct = submit_correct + 1,
               updated_at = now()
         WHERE user_id = v_submitter_id;

        INSERT INTO user_reputation_event(user_id, deal_id, event_type)
        VALUES (v_submitter_id, v_deal_id, 'submit_correct');

        -- Refund penalty if previously rejected and penalized
        IF EXISTS (
          SELECT 1 FROM user_reputation_event
           WHERE user_id = v_submitter_id
             AND deal_id = v_deal_id
             AND deal_product_id IS NULL
             AND event_type = 'submit_penalty'
        )
        AND NOT EXISTS (
          SELECT 1 FROM user_reputation_event
           WHERE user_id = v_submitter_id
             AND deal_id = v_deal_id
             AND deal_product_id IS NULL
             AND event_type = 'submit_penalty_refund'
        ) THEN
          UPDATE user_reputation
             SET penalty_points = GREATEST(0, penalty_points - c_penalty_removed_submission),
                 updated_at = now()
           WHERE user_id = v_submitter_id;

          INSERT INTO user_reputation_event(user_id, deal_id, event_type)
          VALUES (v_submitter_id, v_deal_id, 'submit_penalty_refund');
        END IF;
      END IF;

      -- Submit penalty (removed submission)
      IF v_new_dp_status = 3
         AND NOT EXISTS (
            SELECT 1 FROM user_reputation_event
             WHERE user_id = v_submitter_id
               AND deal_id = v_deal_id
               AND deal_product_id IS NULL
               AND event_type = 'submit_penalty'
         ) THEN
        UPDATE user_reputation
           SET penalty_points = penalty_points + c_penalty_removed_submission,
               updated_at = now()
         WHERE user_id = v_submitter_id;

        INSERT INTO user_reputation_event(user_id, deal_id, event_type)
        VALUES (v_submitter_id, v_deal_id, 'submit_penalty');
      END IF;

      -- Process flaggers (store-wide flags are keyed by deal_id)
      FOR v_flagger_id IN
        SELECT DISTINCT user_id
          FROM deal_flag
         WHERE deal_id = v_deal_id
      LOOP
        INSERT INTO user_reputation (user_id, submit_total, submit_correct, flag_total, flag_true, consecutive_false_flags, penalty_points, updated_at, trust_score)
        VALUES (v_flagger_id,0,0,0,0,0,0, now(), 0)
        ON CONFLICT (user_id) DO NOTHING;

        -- flag_total (one-time)
        IF NOT EXISTS (
          SELECT 1 FROM user_reputation_event
           WHERE user_id = v_flagger_id
             AND deal_id = v_deal_id
             AND deal_product_id IS NULL
             AND event_type = 'flag'
        ) THEN
          UPDATE user_reputation
             SET flag_total = flag_total + 1,
                 updated_at = now()
           WHERE user_id = v_flagger_id;

          INSERT INTO user_reputation_event(user_id, deal_id, event_type)
          VALUES (v_flagger_id, v_deal_id, 'flag');
        END IF;

        IF v_issue_status THEN
          -- Correct flag
          IF NOT EXISTS (
            SELECT 1 FROM user_reputation_event
             WHERE user_id = v_flagger_id
               AND deal_id = v_deal_id
               AND deal_product_id IS NULL
               AND event_type = 'flag_true'
          ) THEN
            UPDATE user_reputation
               SET flag_true = flag_true + 1,
                   consecutive_false_flags = 0,
                   updated_at = now()
             WHERE user_id = v_flagger_id;

            INSERT INTO user_reputation_event(user_id, deal_id, event_type)
            VALUES (v_flagger_id, v_deal_id, 'flag_true');
          END IF;
        ELSE
          -- False flag
          IF NOT EXISTS (
            SELECT 1 FROM user_reputation_event
             WHERE user_id = v_flagger_id
               AND deal_id = v_deal_id
               AND deal_product_id IS NULL
               AND event_type = 'flag_false'
          ) THEN
            UPDATE user_reputation
               SET consecutive_false_flags = consecutive_false_flags + 1,
                   penalty_points = penalty_points + c_penalty_false_flag,
                   updated_at = now()
             WHERE user_id = v_flagger_id;

            INSERT INTO user_reputation_event(user_id, deal_id, event_type)
            VALUES (v_flagger_id, v_deal_id, 'flag_false');
          END IF;
        END IF;
      END LOOP;

      -- Recompute trust_score for all touched users (submitter + flaggers)
      UPDATE user_reputation ur
         SET trust_score =
           GREATEST(0, LEAST(100,
             (ur.submit_correct * 5) + (ur.flag_true * 2) -
             (ur.penalty_points + (ur.consecutive_false_flags * 2))
           )),
             updated_at = now()
       WHERE ur.user_id IN (
         SELECT user_id
           FROM user_reputation_event e
          WHERE e.deal_id = v_deal_id
            AND e.deal_product_id IS NULL
            AND e.event_type IN (
              'submit','submit_correct','submit_penalty',
              'flag','flag_true','flag_false'
            )
       );

      UPDATE public.user u
         SET level = ur.trust_score,
             deals_posted = ur.submit_correct
        FROM user_reputation ur
       WHERE ur.user_id = u.id AND u.admin = false
         AND u.id IN (
           SELECT user_id FROM user_reputation_event
            WHERE deal_id = v_deal_id
              AND deal_product_id IS NULL
         );
    END IF;

    RETURN (SELECT deal_status_id FROM deal WHERE id = v_deal_id);
  END IF;

  -----------------------------------------------------------------------------
  -- Product-deal path (existing behavior)
  -----------------------------------------------------------------------------

  -- Load current info
  SELECT dp.deal_status_id, dp.deal_id, dp.product_id, d.user_id, dp."primary"
    INTO v_old_dp_status, v_deal_id, v_product_id, v_submitter_id, v_primary
  FROM deal_product dp
  JOIN deal d ON d.id = dp.deal_id
  WHERE dp.id = p_deal_product_id;

  IF v_deal_id IS NULL THEN
    RETURN NULL;
  END IF;

  -- Decide new status (admin override > majority >=2)
  WITH reviews AS (
    SELECT dr.deal_status_id, u.admin, dr.created_at
      FROM deal_review dr
      JOIN "user" u ON u.id = dr.user_id
     WHERE dr.deal_product_id = p_deal_product_id
     ORDER BY dr.created_at DESC
  ),
  admin_latest AS (
    SELECT deal_status_id FROM reviews WHERE admin IS TRUE ORDER BY created_at DESC LIMIT 1
  ),
  rev_count AS (
    SELECT count(*) AS cnt FROM reviews
  ),
  majority AS (
    SELECT deal_status_id
      FROM reviews
     GROUP BY deal_status_id
     ORDER BY count(*) DESC, deal_status_id
     LIMIT 1
  )
  SELECT COALESCE(
           (SELECT deal_status_id FROM admin_latest),
           CASE WHEN (SELECT cnt FROM rev_count) >= 2
                THEN (SELECT deal_status_id FROM majority)
           END
         )
    INTO v_new_dp_status;

  IF v_new_dp_status IS NOT NULL AND v_new_dp_status <> v_old_dp_status THEN
    UPDATE deal_product
       SET deal_status_id = v_new_dp_status
     WHERE id = p_deal_product_id;
  END IF;

  -- Update parent deal status
  WITH agg AS (
    SELECT
      BOOL_OR(dp.deal_status_id = 2) AS any_approved,
      MIN(dp.deal_status_id)        AS min_status,
      MAX(dp.deal_status_id)        AS max_status
    FROM deal_product dp
    WHERE dp.deal_id = v_deal_id
      AND COALESCE(dp.deleted,false) = false
  )
  UPDATE deal d
     SET deal_status_id = CASE
         WHEN (SELECT any_approved FROM agg) THEN 2
         WHEN (SELECT min_status FROM agg) = (SELECT max_status FROM agg)
              THEN (SELECT min_status FROM agg)
         ELSE d.deal_status_id
       END
   WHERE d.id = v_deal_id
   RETURNING d.deal_status_id INTO v_parent_deal_status;

  --Remove any stacked deals that use this if it's not active anymore
  IF v_parent_deal_status IS NOT NULL
     AND v_parent_deal_status IN (3,4,6,7) THEN
    UPDATE deal d_main
       SET deal_status_id = 4
     WHERE d_main.id IN (
       SELECT dc.deal_id
         FROM deal_combo dc
        WHERE dc.combo_deal_id = v_deal_id
     );
  END IF;

  -- Reputation adjustments only on actual status change when it's a primary deal_product(Not propagate, added directly)
  IF v_new_dp_status IS NOT NULL AND v_new_dp_status <> v_old_dp_status and v_primary THEN
    -- Issue statuses meaning submission had a problem (adjust if needed)
    v_issue_status := v_new_dp_status IN (3,5);  -- 3 Removed, 5 Review Again

    -- Ensure submitter row
    INSERT INTO user_reputation (user_id, submit_total, submit_correct, flag_total, flag_true, consecutive_false_flags, penalty_points, updated_at, trust_score)
    VALUES (v_submitter_id,0,0,0,0,0,0, now(), 0)
    ON CONFLICT (user_id) DO NOTHING;

    -- Submit total (one-time)
    IF NOT EXISTS (
      SELECT 1 FROM user_reputation_event
       WHERE user_id = v_submitter_id
         AND deal_product_id = p_deal_product_id
         AND event_type = 'submit'
    ) THEN
      UPDATE user_reputation
         SET submit_total = submit_total + 1,
             updated_at = now()
       WHERE user_id = v_submitter_id;

      INSERT INTO user_reputation_event(user_id, deal_product_id, event_type)
      VALUES (v_submitter_id, p_deal_product_id, 'submit');
    END IF;

    -- Submit correct (only if approved)
    IF v_new_dp_status = 2
       AND NOT EXISTS (
          SELECT 1 FROM user_reputation_event
           WHERE user_id = v_submitter_id
             AND deal_product_id = p_deal_product_id
             AND event_type = 'submit_correct'
       ) THEN
      UPDATE user_reputation
         SET submit_correct = submit_correct + 1,
             updated_at = now()
       WHERE user_id = v_submitter_id;

      INSERT INTO user_reputation_event(user_id, deal_product_id, event_type)
      VALUES (v_submitter_id, p_deal_product_id, 'submit_correct');

       -- NEW: refund penalty if this deal was previously rejected and penalized
      IF EXISTS (
        SELECT 1 FROM user_reputation_event
         WHERE user_id = v_submitter_id
           AND deal_product_id = p_deal_product_id
           AND event_type = 'submit_penalty'
      )
      AND NOT EXISTS (
        SELECT 1 FROM user_reputation_event
         WHERE user_id = v_submitter_id
           AND deal_product_id = p_deal_product_id
           AND event_type = 'submit_penalty_refund'
      ) THEN
        UPDATE user_reputation
           SET penalty_points = GREATEST(0, penalty_points - c_penalty_removed_submission),
               updated_at = now()
         WHERE user_id = v_submitter_id;

        INSERT INTO user_reputation_event(user_id, deal_product_id, event_type)
        VALUES (v_submitter_id, p_deal_product_id, 'submit_penalty_refund');
      END IF;
    END IF;

    IF v_new_dp_status = 2 THEN
      --Notify user
      insert into notifications(user_id,type_id,message,link_url) values(v_submitter_id,1,'Your deal was approved!','/profile?dealId=' || v_deal_id::text);
    END IF;

    -- Submit penalty (removed submission only; do NOT penalize for review-again unless you want to)
    IF v_new_dp_status = 3
       AND NOT EXISTS (
          SELECT 1 FROM user_reputation_event
           WHERE user_id = v_submitter_id
             AND deal_product_id = p_deal_product_id
             AND event_type = 'submit_penalty'
       ) THEN
      UPDATE user_reputation
         SET penalty_points = penalty_points + c_penalty_removed_submission,
             updated_at = now()
       WHERE user_id = v_submitter_id;

      INSERT INTO user_reputation_event(user_id, deal_product_id, event_type)
      VALUES (v_submitter_id, p_deal_product_id, 'submit_penalty');

    END IF;

    IF v_new_dp_status = 3 THEN
      --Notify user
      insert into notifications(user_id,type_id,message,link_url) values(v_submitter_id,2,'Your deal was rejected.','/profile?dealId=' || v_deal_id::text);
    END IF;

    -- Process flaggers
    FOR v_flagger_id IN
      SELECT DISTINCT user_id
        FROM deal_flag
       WHERE deal_product_id = p_deal_product_id
    LOOP
      INSERT INTO user_reputation (user_id, submit_total, submit_correct, flag_total, flag_true, consecutive_false_flags, penalty_points, updated_at, trust_score)
      VALUES (v_flagger_id,0,0,0,0,0,0, now(), 0)
      ON CONFLICT (user_id) DO NOTHING;

      -- flag_total (one-time)
      IF NOT EXISTS (
        SELECT 1 FROM user_reputation_event
         WHERE user_id = v_flagger_id
           AND deal_product_id = p_deal_product_id
           AND event_type = 'flag'
      ) THEN
        UPDATE user_reputation
           SET flag_total = flag_total + 1,
               updated_at = now()
         WHERE user_id = v_flagger_id;

        INSERT INTO user_reputation_event(user_id, deal_product_id, event_type)
        VALUES (v_flagger_id, p_deal_product_id, 'flag');
      END IF;

      IF v_issue_status THEN
        -- Correct flag (add flag_true once, reset consecutive_false_flags)
        IF NOT EXISTS (
          SELECT 1 FROM user_reputation_event
           WHERE user_id = v_flagger_id
             AND deal_product_id = p_deal_product_id
             AND event_type = 'flag_true'
        ) THEN
          UPDATE user_reputation
             SET flag_true = flag_true + 1,
                 consecutive_false_flags = 0,
                 updated_at = now()
           WHERE user_id = v_flagger_id;

          INSERT INTO user_reputation_event(user_id, deal_product_id, event_type)
          VALUES (v_flagger_id, p_deal_product_id, 'flag_true');
        END IF;
      ELSE
        -- False flag (approved; no issue). Penalize once.
        IF NOT EXISTS (
          SELECT 1 FROM user_reputation_event
           WHERE user_id = v_flagger_id
             AND deal_product_id = p_deal_product_id
             AND event_type = 'flag_false'
        ) THEN
          UPDATE user_reputation
             SET consecutive_false_flags = consecutive_false_flags + 1,
                 penalty_points = penalty_points + c_penalty_false_flag,
                 updated_at = now()
           WHERE user_id = v_flagger_id;

          INSERT INTO user_reputation_event(user_id, deal_product_id, event_type)
          VALUES (v_flagger_id, p_deal_product_id, 'flag_false');
        END IF;
      END IF;
    END LOOP;

    -- Recompute trust_score for all touched users (submitter + flaggers)
    UPDATE user_reputation ur
       SET trust_score =
         GREATEST(0, LEAST(100,
           (ur.submit_correct * 5) + (ur.flag_true * 2) -
           (ur.penalty_points + (ur.consecutive_false_flags * 2))
         )),
           updated_at = now()
     WHERE ur.user_id IN (
       SELECT user_id
         FROM user_reputation_event e
        WHERE e.deal_product_id = p_deal_product_id
          AND e.event_type IN (
            'submit','submit_correct','submit_penalty',
            'flag','flag_true','flag_false'
          )
     );

     UPDATE public.user u
   SET level = ur.trust_score, deals_posted=ur.submit_correct
  FROM user_reputation ur
 WHERE ur.user_id = u.id and u.admin=false
   AND u.id IN (
     SELECT user_id FROM user_reputation_event
      WHERE deal_product_id = p_deal_product_id
   );
  END IF;

  RETURN (SELECT deal_status_id FROM deal_product WHERE id = p_deal_product_id);
END;
$$;
