ALTER TABLE hm_contacts ADD COLUMN contacturi varchar(255) NOT NULL DEFAULT '';

ALTER TABLE hm_contacts ADD COLUMN contactuid varchar(255) NOT NULL DEFAULT '';

ALTER TABLE hm_contacts ADD COLUMN contactvcard text NOT NULL;

update hm_dbversion set value = 6040;
