ALTER TABLE hm_messages ADD COLUMN messagekeywords varchar(500) NOT NULL DEFAULT '';

update hm_dbversion set value = 6036;
